namespace TlaLink;

public enum ErrorCode
{
    [DiagnosticInfo("SC001", IssueSeverity.Error)]
    OmissionBoundaryConflict,
    [DiagnosticInfo("SC002", IssueSeverity.Warning)]
    OmissionBoundaryIncomplete,
    [DiagnosticInfo("SO001", IssueSeverity.Error)]
    InvalidSourceObligation,
    [DiagnosticInfo("SO100", IssueSeverity.Error)]
    SourceObligationRejected,
    [DiagnosticInfo("SO200", IssueSeverity.Error)]
    SourceObligationIncomplete,
    [DiagnosticInfo("SP001", IssueSeverity.Error)]
    InvalidSourcePreparation,
    [DiagnosticInfo("RP001", IssueSeverity.Error)]
    InvalidReportInput,
    [DiagnosticInfo("RP002", IssueSeverity.Warning)]
    ReportInputStale,
    [DiagnosticInfo("RP003", IssueSeverity.Error)]
    ReportSnapshotChanged,
    [DiagnosticInfo("TL001", IssueSeverity.Error)]
    InvalidAnnotation,
    [DiagnosticInfo("TL003", IssueSeverity.Error)]
    InvalidAttachment,
    [DiagnosticInfo("TL010", IssueSeverity.Error)]
    CSharpSyntaxError,
    [DiagnosticInfo("TL100", IssueSeverity.Error)]
    MissingModelNamespace,
    [DiagnosticInfo("TL101", IssueSeverity.Error)]
    InvalidModelBinding,
    [DiagnosticInfo("TL102", IssueSeverity.Error)]
    DuplicateBinding,
    [DiagnosticInfo("TL103", IssueSeverity.Error)]
    MissingModelEndpoint,
    [DiagnosticInfo("TL104", IssueSeverity.Warning)]
    UnboundModelEndpoint,
    [DiagnosticInfo("TL105", IssueSeverity.Warning)]
    NoLinks,
    [DiagnosticInfo("TL200", IssueSeverity.Warning)]
    UncoveredStateWrite,
    [DiagnosticInfo("TL202", IssueSeverity.Warning)]
    UnresolvedCompilation,
    [DiagnosticInfo("TL300", IssueSeverity.Warning)]
    ReviewInputChanged,
    [DiagnosticInfo("TL400", IssueSeverity.Warning)]
    StaleTranslation,
    [DiagnosticInfo("TL500", IssueSeverity.Error)]
    UnresolvedInstrumentation,
    [DiagnosticInfo("TL501", IssueSeverity.Error)]
    InvalidInstrumentation,
    [DiagnosticInfo("TL510", IssueSeverity.Error)]
    InvalidTrace,
    [DiagnosticInfo("TL511", IssueSeverity.Error)]
    TraceRejected,
    [DiagnosticInfo("TL520", IssueSeverity.Error)]
    InvalidExecution,
    [DiagnosticInfo("CS100", IssueSeverity.Error)]
    RegressionAssertionFailed,
    [DiagnosticInfo("TL900", IssueSeverity.Error)]
    ToolFailure,

    [DiagnosticInfo("GC001", IssueSeverity.Error)]
    InvalidRetention,
    [DiagnosticInfo("GC002", IssueSeverity.Error)]
    RetentionScanIncomplete,
    [DiagnosticInfo("GC003", IssueSeverity.Error)]
    RetentionApplyIncomplete,

    [DiagnosticInfo("DF001", IssueSeverity.Error)]
    InvalidIntegratedInput,
    [DiagnosticInfo("DF002", IssueSeverity.Error)]
    IntegratedInputChanged,
    [DiagnosticInfo("DF010", IssueSeverity.Error)]
    InvalidDafnyTool,
    [DiagnosticInfo("DF011", IssueSeverity.Error)]
    DafnyResolutionFailed,
    [DiagnosticInfo("DF020", IssueSeverity.Error)]
    InvalidIntegratedBinding,
    [DiagnosticInfo("DF021", IssueSeverity.Error)]
    IntegratedTlaLinksNotReady,
    [DiagnosticInfo("DF022", IssueSeverity.Error)]
    InvalidCorrespondence,
    [DiagnosticInfo("DF030", IssueSeverity.Warning)]
    IntegratedBaselineChanged,
    [DiagnosticInfo("DF100", IssueSeverity.Error)]
    DafnyVerificationFailed,
    [DiagnosticInfo("DF101", IssueSeverity.Error)]
    DafnyAuditFailed,
    [DiagnosticInfo("DF200", IssueSeverity.Error)]
    InvalidOracleInput,
    [DiagnosticInfo("DF201", IssueSeverity.Error)]
    OraclePreconditionsFailed,
    [DiagnosticInfo("DF202", IssueSeverity.Error)]
    OracleCompilationFailed,
    [DiagnosticInfo("DF203", IssueSeverity.Error)]
    OracleExecutionFailed,
    [DiagnosticInfo("DF204", IssueSeverity.Error)]
    OracleComparisonFailed,
    [DiagnosticInfo("DF300", IssueSeverity.Error)]
    InvalidIntegratedEvidence,
    [DiagnosticInfo("DF301", IssueSeverity.Error)]
    IntegratedArchiveInconsistent,
    [DiagnosticInfo("DF302", IssueSeverity.Warning)]
    IntegratedEvidenceStale,
    [DiagnosticInfo("DF303", IssueSeverity.Error)]
    IntegratedEvidenceInconsistent,
    [DiagnosticInfo("DF900", IssueSeverity.Error)]
    IntegratedToolFailure,

    [DiagnosticInfo("PL001", IssueSeverity.Error)]
    InvalidPInput,
    [DiagnosticInfo("PL002", IssueSeverity.Error)]
    PInputChanged,
    [DiagnosticInfo("PL010", IssueSeverity.Error)]
    PPrerequisiteMissing,
    [DiagnosticInfo("PL020", IssueSeverity.Error)]
    InvalidPBinding,
    [DiagnosticInfo("PL100", IssueSeverity.Error)]
    PAssertionFailed,
    [DiagnosticInfo("PL101", IssueSeverity.Error)]
    PLivenessFailed,
    [DiagnosticInfo("PL102", IssueSeverity.Error)]
    PInvariantFailed,
    [DiagnosticInfo("PL103", IssueSeverity.Error)]
    PMonitorFailed,
    [DiagnosticInfo("PL104", IssueSeverity.Error)]
    PExplorationIncomplete,
    [DiagnosticInfo("PL300", IssueSeverity.Error)]
    InvalidPEvidence,
    [DiagnosticInfo("PL301", IssueSeverity.Warning)]
    PEvidenceStale,
    [DiagnosticInfo("PL900", IssueSeverity.Error)]
    PToolFailure,

    [DiagnosticInfo("AP001", IssueSeverity.Error)]
    InvalidApalacheInput,
    [DiagnosticInfo("AP010", IssueSeverity.Error)]
    ApalachePrerequisiteMissing,
    [DiagnosticInfo("AP100", IssueSeverity.Error)]
    ApalachePropertyRejected,
    [DiagnosticInfo("AP101", IssueSeverity.Error)]
    ApalacheInductionRejected,
    [DiagnosticInfo("AP300", IssueSeverity.Error)]
    InvalidApalacheEvidence,
    [DiagnosticInfo("AP301", IssueSeverity.Warning)]
    ApalacheEvidenceStale,
    [DiagnosticInfo("AP900", IssueSeverity.Error)]
    ApalacheToolFailure,

    [DiagnosticInfo("WF001", IssueSeverity.Error)]
    InvalidWorkflowInput,
    [DiagnosticInfo("WF002", IssueSeverity.Error)]
    WorkflowPrerequisiteMissing,
    [DiagnosticInfo("WF003", IssueSeverity.Error)]
    DependencyBlocked,
    [DiagnosticInfo("WF004", IssueSeverity.Error)]
    WorkflowInputChanged,
    [DiagnosticInfo("WF005", IssueSeverity.Error)]
    InvalidCommandOutput,
    [DiagnosticInfo("WF006", IssueSeverity.Error)]
    InvalidArchivePath,
    [DiagnosticInfo("WF010", IssueSeverity.Error)]
    WorkflowProjectChanged,
    [DiagnosticInfo("WF011", IssueSeverity.Error)]
    WorkflowExecutionIncomplete,
    [DiagnosticInfo("WF012", IssueSeverity.Error)]
    WorkflowProvenanceInconsistent,
    [DiagnosticInfo("WF013", IssueSeverity.Error)]
    WorkflowEvidenceStale,
    [DiagnosticInfo("WF014", IssueSeverity.Error)]
    WorkflowStageInconsistent,
    [DiagnosticInfo("WF015", IssueSeverity.Error)]
    WorkflowRequirementUnsatisfied,
    [DiagnosticInfo("WF900", IssueSeverity.Error)]
    WorkflowStageFailed
}
