using System.Text.Json.Nodes;
using BDIT.TenantToolkit.Core;
using BDIT.TenantToolkit.Core.Diagnostics;
using BDIT.TenantToolkit.Core.Graph;
using BDIT.TenantToolkit.Core.Json;
using BDIT.TenantToolkit.Core.Models;
using BDIT.TenantToolkit.Core.Safety;
using BDIT.TenantToolkit.Engine.Assessment;
using BDIT.TenantToolkit.Engine.Collection;
using BDIT.TenantToolkit.Engine.Evidence;

namespace BDIT.TenantToolkit.Engine.Execution;

public sealed class ExecutionRequest
{
    public required DeploymentPlan Plan { get; init; }
    public required TenantProfile Profile { get; init; }
    public required StandardCatalogue Standard { get; init; }
    public required TenantSnapshot Snapshot { get; init; }
    public required ManagedObjectMappings Mappings { get; init; }
    public required TenantSession Session { get; init; }
    public required IGraphClient Graph { get; init; }
}

/// <summary>
/// Executes a validated plan one write at a time: reconcile live state, record intent, write, read back, map, and
/// capture after-state evidence. Any ambiguity stops the run for review. The executor tracks its in-flight task so the
/// host can refuse to exit while a write is outstanding.
/// </summary>
public sealed class DeploymentExecutor
{
    private readonly EvidenceStore _evidence;
    private readonly TenantCollector _collector;
    private readonly IToolkitLog _log;
    private readonly IClock _clock;
    private readonly string _toolkitVersion;
    private readonly object _gate = new();
    private long _journalSequence;

    public DeploymentRun? CurrentRun { get; private set; }
    public Task? CurrentTask { get; private set; }
    public bool IsRunning => CurrentTask is { IsCompleted: false };

    public DeploymentExecutor(EvidenceStore evidence, TenantCollector collector, IToolkitLog log, IClock clock, string toolkitVersion)
    {
        _evidence = evidence;
        _collector = collector;
        _log = log;
        _clock = clock;
        _toolkitVersion = toolkitVersion;
    }

    public Task<DeploymentRun> StartAsync(ExecutionRequest request, DeploymentControl control, IProgress<string>? progress)
    {
        lock (_gate)
        {
            if (IsRunning) throw new ToolkitException("A deployment is already running.");
            var task = ExecuteAsync(request, control, progress);
            CurrentTask = task;
            return task;
        }
    }

    /// <summary>Waits for an in-flight run to reach a safe boundary and finish its evidence. Used by application shutdown.</summary>
    public async Task WaitForCompletionAsync(DeploymentControl? control)
    {
        control?.Stop();
        var task = CurrentTask;
        if (task is not null)
        {
            try { await task; } catch (Exception) { }
        }
    }

    private async Task<DeploymentRun> ExecuteAsync(ExecutionRequest request, DeploymentControl control, IProgress<string>? progress)
    {
        var plan = request.Plan;
        var profile = request.Profile;
        var session = request.Session;
        var graph = request.Graph;
        if (graph.Mode != SessionMode.Deployment) throw new WriteDeniedException("The Graph session is read-only; deployment refused.");
        if (!string.Equals(graph.TenantId, plan.TenantId, StringComparison.OrdinalIgnoreCase)) throw new TenantMismatchException("The Graph session is connected to a different tenant than the plan.");

        var run = new DeploymentRun
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = plan.TenantId,
            TenantName = request.Snapshot.TenantName,
            PrimaryDomain = request.Snapshot.PrimaryDomain,
            Release = plan.Release,
            PlanId = plan.Id,
            PlanDigest = plan.PlanDigest,
            BeforeSnapshotId = request.Snapshot.Id,
            StartedAt = Timestamps.Format(_clock.UtcNow),
            ToolkitVersion = _toolkitVersion,
            Actor = new RunActor { Account = session.Account, ObjectId = session.OperatorObjectId ?? "", ClientId = session.ClientId, AuthenticationType = session.AuthenticationType },
            Results = plan.Rows.Select(r => new RunResult
            {
                ControlId = r.ControlId,
                Name = r.Name,
                Collection = r.Collection,
                PlannedAction = r.Action.ToString(),
                Status = r.IsWrite ? ResultStatus.Pending : r.Action.ToString(),
                Reason = r.IsWrite ? null : r.Reason
            }).ToList()
        };
        CurrentRun = run;
        _evidence.SaveRun(run);
        Journal(run, "Info", $"Run started for plan {plan.Id} (digest {plan.PlanDigest[..12]}…) by {session.Account}.");

        var mappings = request.Mappings;
        var failed = false;
        var stopped = false;
        var ct = CancellationToken.None;

        try
        {
            foreach (var row in plan.WriteRows)
            {
                await control.WaitWhilePausedAsync(ct);
                if (control.StopRequested) { stopped = true; break; }
                var result = run.Results.First(r => r.ControlId == row.ControlId);
                var def = request.Standard.FindCollection(row.Collection) ?? throw new PlanValidationException($"Unknown collection {row.Collection}.");
                var payload = row.Payload ?? throw new PlanValidationException($"{row.ControlId}: missing payload.");
                try
                {
                    progress?.Report($"Preflight {row.ControlId}");
                    Journal(run, "Info", $"Preflight {row.ControlId}: re-reading live state.", row.ControlId);
                    await PreflightAsync(graph, def, row, mappings, ct);

                    WritePayloadGuard.Assert(def, payload);
                    if (control.StopRequested) { stopped = true; break; }

                    result.Status = ResultStatus.InProgress;
                    result.PayloadDigest = CanonicalJson.Sha256(payload);
                    _evidence.SaveRun(run);
                    Journal(run, "Info", $"WRITE INTENT {row.ControlId} {row.Action} payloadDigest={result.PayloadDigest}", row.ControlId);
                    progress?.Report($"Writing {row.ControlId}");

                    var path = row.Action == PlanAction.Update ? def.BasePath.TrimEnd('/') + "/" + row.ObjectId : def.BasePath;
                    var method = row.Action == PlanAction.Update ? GraphWriteMethod.Patch : GraphWriteMethod.Post;
                    JsonObject response;
                    try
                    {
                        response = await graph.WriteAsync(def.ApiVersion, method, path, payload, ct);
                    }
                    catch (AmbiguousWriteException ex)
                    {
                        result.Status = ResultStatus.Error;
                        result.Reason = ex.Message;
                        result.Configuration = ConfigurationVerification.Unknown;
                        failed = true;
                        Journal(run, "Error", $"{row.ControlId}: ambiguous write outcome. {ex.Message}", row.ControlId);
                        break;
                    }
                    result.WrittenAt = Timestamps.Format(_clock.UtcNow);

                    var objectId = row.Action == PlanAction.Update ? row.ObjectId : response["id"]?.GetValue<string>();
                    if (!ProfileValidator.IsGuid(objectId ?? ""))
                    {
                        result.Status = ResultStatus.Error;
                        result.Reason = "The write response did not include a valid object ID. Outcome unknown; do not retry blindly. Reassess the tenant.";
                        result.Configuration = ConfigurationVerification.Unknown;
                        failed = true;
                        Journal(run, "Error", $"{row.ControlId}: {result.Reason}", row.ControlId);
                        break;
                    }
                    var id = objectId!.ToLowerInvariant();

                    var existing = mappings.Find(row.ControlId);
                    mappings.ByControl[row.ControlId] = new ManagedObjectMapping
                    {
                        ControlId = row.ControlId,
                        ObjectId = id,
                        Collection = row.Collection,
                        LastApplied = (JsonObject)payload.DeepClone(),
                        LastAppliedDigest = result.PayloadDigest,
                        Release = plan.Release,
                        RunId = run.Id,
                        CreatedAt = existing?.CreatedAt ?? result.WrittenAt,
                        UpdatedAt = result.WrittenAt,
                        OperatorExclusion = row.OperatorExclusion ?? existing?.OperatorExclusion
                    };
                    _evidence.SaveMappings(mappings);
                    result.ObjectId = id;
                    result.Status = ResultStatus.Completed;
                    _evidence.SaveRun(run);
                    Journal(run, "Info", $"{row.ControlId}: write accepted; object {id} recorded as toolkit-managed.", row.ControlId);

                    progress?.Report($"Reading back {row.ControlId}");
                    var readback = await graph.GetAsync(def.ApiVersion, def.BasePath.TrimEnd('/') + "/" + id, ct);
                    if (!string.IsNullOrEmpty(def.Relationship))
                    {
                        var relationshipName = def.Relationship.Split('?')[0].Trim('/');
                        readback[relationshipName] = ToArray(await graph.GetAllAsync(def.ApiVersion, def.BasePath.TrimEnd('/') + "/" + id + "/" + def.Relationship, ct));
                    }
                    result.ReadbackDigest = CanonicalJson.Sha256(readback);
                    var pass = CanonicalJson.IsSubset(readback, payload);
                    result.Configuration = pass ? ConfigurationVerification.Pass : ConfigurationVerification.Unknown;
                    _evidence.SaveRun(run);
                    if (!pass)
                    {
                        result.Reason = "The readback did not confirm every requested setting. The object exists; review it manually before continuing.";
                        failed = true;
                        Journal(run, "Warning", $"{row.ControlId}: readback mismatch; run stopped for review.", row.ControlId);
                        break;
                    }
                    Journal(run, "Completed", $"{row.ControlId} completed and configuration readback passed.", row.ControlId);
                }
                catch (Exception ex) when (ex is ToolkitException)
                {
                    result.Status = ResultStatus.Error;
                    result.Reason = ex.Message;
                    failed = true;
                    Journal(run, "Error", $"{row.ControlId} failed: {ex.Message}", row.ControlId);
                    _log.Error("Deploy", $"{row.ControlId} failed.", ex, plan.TenantId, row.ControlId);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            failed = true;
            run.Error = "Execution failed: " + ex.Message;
            _log.Error("Deploy", "Execution failed.", ex, plan.TenantId);
        }
        finally
        {
            foreach (var r in run.Results) if (r.Status == ResultStatus.Pending) r.Status = ResultStatus.NotRun;
            try
            {
                progress?.Report("Capturing after-change snapshot");
                Journal(run, "Info", "Collecting after-change snapshot.");
                var after = await _collector.CollectAsync(graph, session, profile, request.Standard, null, ct);
                _evidence.SaveSnapshot(after);
                run.AfterSnapshotId = after.Id;
                run.AfterComplete = after.Complete;
                if (!after.Complete) failed = true;
            }
            catch (Exception ex)
            {
                run.AfterError = ex.Message;
                failed = true;
                _log.Error("Deploy", "After-change snapshot failed.", ex, plan.TenantId);
            }
            run.Status = failed ? RunStatus.ReviewRequired : stopped ? RunStatus.Stopped : RunStatus.Completed;
            run.EndedAt = Timestamps.Format(_clock.UtcNow);
            _evidence.SaveRun(run);
            Journal(run, failed ? "Warning" : "Completed", $"Run ended with status '{run.Status}'.");
            lock (_gate) { CurrentTask = null; }
        }
        return run;
    }

    private async Task PreflightAsync(IGraphClient graph, CollectionDefinition def, PlanRow row, ManagedObjectMappings mappings, CancellationToken ct)
    {
        var isConditionalAccess = ConditionalAccessSafety.IsConditionalAccess(def);
        if (row.Action == PlanAction.Update)
        {
            var objectId = row.ObjectId ?? throw new PlanValidationException("Update row without object ID.");
            var mapping = mappings.Find(row.ControlId) ?? throw new SafetyViolationException($"{row.ControlId}: no ownership mapping for object {objectId}; refusing to update an object the toolkit does not own.");
            if (!string.Equals(mapping.ObjectId, objectId, StringComparison.OrdinalIgnoreCase))
                throw new SafetyViolationException($"{row.ControlId}: the plan targets {objectId} but the toolkit owns {mapping.ObjectId}. Rebuild the plan.");
            var actual = await graph.GetAsync(def.ApiVersion, def.BasePath.TrimEnd('/') + "/" + objectId, ct);
            if (!string.IsNullOrEmpty(def.Relationship))
            {
                var relationshipName = def.Relationship.Split('?')[0].Trim('/');
                actual[relationshipName] = ToArray(await graph.GetAllAsync(def.ApiVersion, def.BasePath.TrimEnd('/') + "/" + objectId + "/" + def.Relationship, ct));
            }
            if (mapping.LastApplied is not null && !CanonicalJson.IsSubset(actual, mapping.LastApplied))
                throw new SafetyViolationException($"{row.ControlId}: live drift detected since planning; the object no longer matches what the toolkit last applied.");
            if (isConditionalAccess && !string.Equals(actual["state"]?.GetValue<string>(), ConditionalAccessSafety.SafeState, StringComparison.OrdinalIgnoreCase))
                throw new SafetyViolationException($"{row.ControlId}: the policy is no longer disabled; the toolkit does not modify active Conditional Access policies.");
            if (def.Assignments)
            {
                var assignments = await graph.GetAllAsync(def.ApiVersion, def.BasePath.TrimEnd('/') + "/" + objectId + "/assignments", ct);
                if (assignments.Count > 0) throw new SafetyViolationException($"{row.ControlId}: the object has been assigned since planning; inactive-only updates are supported.");
            }
            return;
        }

        var current = await graph.GetAllAsync(def.ApiVersion, def.Path, ct);
        var nameKey = def.NameProperty;
        var proposedName = row.Payload?[nameKey]?.GetValue<string>() ?? "";
        if (current.Any(o => string.Equals(o[nameKey]?.GetValue<string>(), proposedName, StringComparison.OrdinalIgnoreCase)))
            throw new SafetyViolationException($"{row.ControlId}: an object named '{proposedName}' appeared since the snapshot was taken. Reconcile before creating.");
        if (row.Payload is not null)
        {
            var overlapping = AssessmentEngine.FindCandidates(current, row.Payload, new AssessmentRule { Mode = AssessmentMode.Settings }, def, new NameResolver(), mappings)
                .Where(c => c.SettingsMatch).ToList();
            if (overlapping.Count > 0)
                throw new SafetyViolationException($"{row.ControlId}: an object with matching settings appeared since the snapshot was taken ({string.Join(", ", overlapping.Select(c => c.Name))}). Reconcile before creating.");
        }
    }

    private void Journal(DeploymentRun run, string level, string message, string? controlId = null)
    {
        var entry = new JournalEntry
        {
            Sequence = Interlocked.Increment(ref _journalSequence),
            At = Timestamps.Format(_clock.UtcNow),
            Level = level,
            Message = SensitiveDataScrubber.Scrub(message),
            ControlId = controlId
        };
        _evidence.AppendJournal(run.TenantId, run.Id, entry);
        _log.Log(level == "Error" ? LogLevel.Error : level == "Warning" ? LogLevel.Warning : LogLevel.Information, "Deploy", message, run.TenantId, controlId);
    }

    private static JsonArray ToArray(IReadOnlyList<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return array;
    }
}
