namespace TlaLink;

public static class CommandSummary
{
    public static CommandSummaryResult Create(object value, CliOptions options) => Create(value, options, null);

    internal static CommandSummaryResult Create(object value, CliOptions options, AcceptanceWorklist? worklist)
    {
        CommandSummaryResult result = value switch {
            AcceptanceResult acceptance => Acceptance(acceptance, worklist),
            WorkflowRun run => Workflow(run),
            ProjectPlan plan => new ProjectPlanSummary(plan.Kind, plan.Project, plan.Status, plan.AnnotationVersion,
                plan.MethodSummaries.Select(s => new PlannedMethodSummaryView(s.Scope, s.Summary, s.Method,
                    s.InspectionStatus, s.InspectionOrigin, s.Boundary)).ToList(),
                plan.CheckCoverage.Select(Coverage).ToList(), plan.Diagnostics,
                plan.Scenarios.Select(s => new PlannedScenarioSummary(s.Id, s.Stages.Select(stage =>
                    new PlannedStageSummary(stage.Id, stage.Kind, stage.DependsOn, stage.RequestedResources,
                        stage.Resources, stage.Fingerprint, stage.Stored, stage.MethodSummaries)).ToList())).ToList(),
                plan.ReviewStatus, plan.Boundary),
            SourceInventoryResult inventory => new SourceInventorySummary(inventory.Status, inventory.AnnotationVersion,
                inventory.SourceCount, inventory.MemberCount, inventory.CallCount, inventory.AwaitCount,
                inventory.Compiler, inventory.Diagnostics, inventory.Boundaries),
            SourceSymbolsResult symbols => new SourceSymbolsSummary(symbols.Status, symbols.Query,
                symbols.MatchCount, symbols.Symbols, symbols.Compiler, symbols.Sources, symbols.Details,
                symbols.ReferenceCount, symbols.CallCount, symbols.Diagnostics, symbols.Boundaries),
            Packet packet => new SourceReviewSummary(packet.Command, packet.LinksStatus, packet.CoverageStatus,
                packet.ConformanceStatus, packet.Diagnostics),
            IntegratedPacket packet => new IntegratedSummary(packet.Command, packet.Status, packet.LinksStatus,
                packet.CoverageStatus, packet.ProofStatus, packet.AuditStatus, packet.ConformanceStatus,
                packet.CorrespondenceStatus, packet.SourceObligationsStatus, packet.ProvenanceStatus,
                packet.SourceObligations.Select(o => new SourceObligationSummary(o.Obligation, o.Status, o.Reason)).ToList(),
                packet.Stages, packet.Diagnostics, packet.Boundaries),
            IntegratedResult packet => new IntegratedSummary(packet.Command, packet.Status, packet.LinksStatus,
                packet.CoverageStatus, packet.ProofStatus, packet.AuditStatus, packet.ConformanceStatus,
                packet.CorrespondenceStatus, packet.SourceObligationsStatus,
                packet.ProvenanceStatus,
                packet.SourceObligations.Select(o => new SourceObligationSummary(o.Obligation, o.Status, o.Reason)).ToList(),
                packet.Stages.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                packet.Diagnostics.ToList(), packet.Boundaries.ToList()),
            ApalachePacket packet => new ApalacheSummary(packet.Status, packet.Claim, packet.Mode, packet.Directory,
                packet.Processes.Select(p => new ProcessSummary(p.Id, p.ExitCode, p.TimedOut, p.DurationSeconds)).ToList(),
                packet.Diagnostics, packet.Boundaries),
            PPacket packet => new PAnalysisSummary(packet.Command, packet.Status, packet.Claim, packet.LinksStatus,
                packet.ProvenanceStatus, packet.Processes.Select(p => new ProcessSummary(p.Label, p.ExitCode,
                    p.TimedOut, null)).ToList(), packet.Diagnostics, packet.Boundaries),
            DafnyResult native => new DafnySummary(native.Status, native.Claim, native.Directory, native.ProofStatus,
                native.AuditStatus, native.Stages, native.Diagnostics, native.Boundary),
            CSharpRegressionResult regression => new RegressionSummary(regression.Status, regression.Claim,
                regression.Directory, regression.Diagnostics, regression.Boundary),
            IntegratedEvidenceInspection inspection => new EvidenceInspectionSummary(inspection.Kind,
                inspection.ArchiveStatus, inspection.CurrentApplicability, inspection.Diagnostics,
                inspection.SemanticStatus, inspection.RecordedStatus, inspection.AcceptanceStatus, Boundary: inspection.Boundary),
            StageEvidenceInspection inspection => new EvidenceInspectionSummary(inspection.Kind,
                inspection.ArchiveStatus, inspection.CurrentApplicability, inspection.Diagnostics,
                inspection.SemanticStatus, inspection.RecordedStatus, inspection.AcceptanceStatus, Boundary: inspection.Boundary),
            PEvidenceResult inspection => new EvidenceInspectionSummary(inspection.Kind, inspection.ArchiveStatus,
                inspection.CurrentApplicability, inspection.Diagnostics, Status: inspection.Status),
            ApalacheEvidenceResult inspection => new EvidenceInspectionSummary(inspection.Kind,
                inspection.ArchiveStatus, inspection.CurrentApplicability, inspection.Diagnostics,
                AnalysisStatus: inspection.AnalysisStatus, Boundary: inspection.Boundary),
            PreparedSources prepared => new PreparedSummary(prepared.Kind, prepared.Status,
                prepared.FileCount, prepared.MutatedFileCount, prepared.Boundary),
            PreparedSourcesInspection inspection => new PreparedSummary(inspection.Kind, inspection.Status,
                inspection.FileCount, inspection.MutatedFileCount, inspection.Boundary),
            ReportResult report => new ReportSummary(report.Status, report.CurrentApplicability, report.Nodes,
                report.CodeRegions, report.InputFiles, report.Diagnostics, report.Boundary) { Output = report.Output },
            ProjectSchemaResult schema => new SchemaSummary(schema.Status, schema.Path),
            RecordedReview review => new RecordedReviewSummary(review.Path, new(review.Decision.Obligation,
                review.Decision.Disposition, review.Decision.Reviewer, review.Decision.Rationale,
                review.Decision.Origin), review.Boundary),
            ModelInputsResult inputs => new ModelInputsSummary(inputs.Command, inputs.Status, inputs.Diagnostics),
            InstrumentManifest manifest => new InstrumentationSummary(manifest.AnnotationVersion, manifest.Boundary),
            ProjectExpansion expansion => new ProjectExpansionSummary(expansion.Project),
            LinkMaintenanceResult links => new LinkMaintenanceSummary(links.Action, links.Status, links.Target,
                links.Language, links.Links.Select(l => new MaintainedLinkSummary(l.Binding.Id, l.Binding.Reason,
                    l.Binding.Source, new(l.Endpoint.Name, l.Endpoint.Scope, l.Endpoint.Kind, l.Endpoint.Location),
                    l.Captures)).ToList(), links.Edit, links.Diagnostics, links.Boundary),
            LinkMigrationResult migration => new LinkMigrationSummary(migration.Status, migration.Scope,
                migration.ReadOnly, migration.StripSource, migration.BindingCount, migration.ExclusionCount,
                migration.Files, migration.Applied, migration.Boundary),
            GcPlan plan => GarbageCollection(plan),
            GcApplyResult apply => new GcApplySummary(apply.Status, apply.Store, apply.Plan, apply.StartedAt,
                apply.ReadOnly, apply.DeletionAuthorized, apply.DeletedUnits, apply.PendingUnits,
                apply.PartialUnit, apply.RemovedFileCount, apply.Diagnostics, apply.Boundaries),
            CommandFailure failure => new FailureSummary(failure.Command, failure.Status, failure.Diagnostics),
            ExecutionReceipt execution => new ExecutionSummary(execution.Boundary),
            TraceValidationResult trace => new TraceSummary(trace.Command, trace.Status, trace.Diagnostics,
                trace.RequestedResources, trace.Resources),
            TlcResult tlc => new TlcSummary(tlc.Status, tlc.Diagnostics, tlc.Fingerprint,
                tlc.RequestedResources, tlc.Resources, tlc.Statistics, tlc.Boundaries),
            StageCheckResult check => new StageCheckSummary(check.Project, check.Scenario, check.Stage,
                check.Status, check.Outcome, check.Fingerprint, check.Key, check.Family, check.Entry,
                check.DurationSeconds, check.RequestedResources, check.Resources, check.TimeoutSeconds,
                check.Statistics, check.Diagnostics, check.Boundary),
            _ => throw new InputException(ErrorCode.InvalidCommandOutput,
                "No typed command summary is declared for " + value.GetType().Name + ".")
        };
        List<string> runs = options.Many("run").Select(FilePaths.Full).ToList();
        return result with {
            Output = options.Optional("output") is string output ? FilePaths.Full(output) : result.Output,
            Result = options.Optional("result") is string receipt ? FilePaths.Full(receipt) : result.Result,
            Run = runs.Count == 1 ? runs[0] : result.Run,
            Runs = runs.Count > 1 ? runs : null
        };
    }

    private static CoverageSummary Coverage(CheckCoverage c) =>
        new(c.Id, c.Kind, c.Scenario, c.Status, c.Requirements, c.Gaps);

    private static AcceptanceSummary Acceptance(AcceptanceResult result, AcceptanceWorklist? worklist)
    {
        AcceptanceSummary summary = new(result.Project, result.Scenario, result.ExecutionStatus,
            result.ArchiveStatus, result.CurrentApplicability, result.AcceptanceStatus,
            result.Requirements.Select(r => new RequirementSummary(r.Id, r.Status, r.Missing)).ToList(),
            result.Diagnostics, result.CheckCoverage.Select(Coverage).ToList(), result.MethodSummaries,
            result.Reviews.Select(r => new ReviewSummary(r.Obligation, r.Status, r.Decision?.Disposition,
                r.Decision?.Reviewer, r.Decision?.Rationale, r.Decision?.RecordedAt, r.Decision?.Origin)).ToList(),
            result.NextActions, result.Boundary) { Run = result.Run };
        return worklist == null ? summary : worklist.Apply(summary);
    }

    private static WorkflowRunSummary Workflow(WorkflowRun run)
    {
        List<WorkflowStageSummary> stages = run.Stages.Select(s => new WorkflowStageSummary(s.Id, s.Kind,
            s.ExecutionStatus, s.Outcome, s.DurationSeconds, s.Evidence, s.Diagnostics, s.Statistics,
            s.RequestedResources, s.Resources, s.StoredResults?.Any(r => r.Primary && r.Origin == "reused") == true)).ToList();
        int reused = stages.Count(s => s.Reused);
        return new(run.Project, run.Scenario, run.ExecutionStatus, run.AcceptanceStatus, stages,
            run.Diagnostics, new(reused, stages.Count - reused),
            run.MethodSummaries.Select(s => new ResolvedMethodSummaryView(s.Scope, s.Summary, s.Method,
                s.Precondition is { } p ? new(p.Name, p.Scope, p.Kind, p.Location) : null)).ToList());
    }

    private static GcPlanSummary GarbageCollection(GcPlan plan)
    {
        const int limit = 10;
        return new(plan.Status, plan.Store, plan.Retention, plan.PlannedAt, plan.GraceHours,
            plan.ReadOnly, plan.DeletionAuthorized, plan.Counts,
            plan.Entries.Take(limit).Select(e => new GcEntrySummary(e.Path, e.Status, e.LogicalBytes,
                e.RecheckRequired, e.Reasons.Count, e.Dependencies.Count)).ToList(),
            plan.ProtectedPaths.Take(limit).ToList(),
            plan.ChangedInputs.Take(limit).Select(i => new GcInputSummary(i.Path, i.Status, i.Consumers.Count)).ToList(),
            plan.Recheck.Take(limit).Select(r => new GcRecheckSummary(r.Path, r.Kind, r.Scope,
                r.ChangedInputs.Count, r.AffectedBy.Count, r.Actions)).ToList(),
            plan.Diagnostics.Take(limit).ToList(), plan.Boundaries,
            new(StringComparer.Ordinal) {
                ["entries"] = Math.Max(0, plan.Entries.Count - limit),
                ["protected_paths"] = Math.Max(0, plan.ProtectedPaths.Count - limit),
                ["changed_inputs"] = Math.Max(0, plan.ChangedInputs.Count - limit),
                ["recheck"] = Math.Max(0, plan.Recheck.Count - limit),
                ["diagnostics"] = Math.Max(0, plan.Diagnostics.Count - limit)
            });
    }
}
