namespace Backspace.Services;

/// <summary>
/// Marker interface — exceptions thrown from an <see cref="IBackupEngine"/>
/// implementation that the orchestrator must NOT retry on. Authentication
/// failures, missing-storage errors, repo-already-initialized conditions
/// all fall in this bucket: re-running the same operation will produce
/// the same failure, so retrying just wastes time and noise.
///
/// Lives in its own file so the orchestrator's catch block survives
/// engine swaps — earlier revisions kept this next to per-engine
/// exception types and any engine rename forced a touch here.
/// </summary>
public interface INonRetriable { }

/// <summary>
/// Marker interface — exceptions thrown from an <see cref="IBackupEngine"/>
/// implementation that represent a TOLERABLE condition the user can't
/// act on directly: laptop offline, NAS unreachable, external drive
/// unplugged. The orchestrator surfaces these as <c>Skipped</c> (not
/// Failed) so the card doesn't go red on a normal away-from-home night.
///
/// Implementations are also <see cref="INonRetriable"/> — retrying a
/// tolerable failure within the same cell's retry budget doesn't
/// recover; the user has to come home / plug the drive back in.
///
/// The classification gate is <see cref="EngineErrorClassifier"/>: every
/// engine-level failure flows through it, and a TOLERABLE verdict gets
/// re-thrown as this marker so the cell loop can route it correctly.
/// </summary>
public interface ITolerable { }

/// <summary>An interrupted repository initialization left encryption keys behind.
/// Repeating initialization cannot recover that repository and must not replace it.</summary>
public sealed class EngineRepositoryInitializationException : System.Exception, INonRetriable
{
    public EngineRepositoryInitializationException(string message, System.Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a required engine binary (backrest, restic, rclone)
/// isn't on disk next to Backspace.exe. The orchestrator treats this
/// as non-retriable — retrying a missing binary is pure delay — and
/// surfaces the message verbatim to the user so they know exactly
/// what to fix.
/// </summary>
public sealed class EngineBinaryMissingException : System.IO.FileNotFoundException, INonRetriable
{
    public EngineBinaryMissingException(string message, string fileName) : base(message, fileName) { }
}

/// <summary>
/// Thrown when the engine reports that the plan or repo we just
/// asked it to operate on doesn't exist in its config. Retrying
/// the orchestrator-level cell can't recover from this — the
/// engine's internal config is out of sync with what we just
/// asked for, and no amount of backoff fixes that. We surface
/// the underlying RPC message so the cell-log breadcrumb tells
/// the operator what to investigate (most often a stale Backrest
/// data dir from a prior install).
/// </summary>
public sealed class EngineConfigOutOfSyncException : System.Exception, INonRetriable
{
    public EngineConfigOutOfSyncException(string message) : base(message) { }
    public EngineConfigOutOfSyncException(string message, System.Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when the engine reports an authentication / authorization
/// failure (Connect-Go code <c>unauthenticated</c> or
/// <c>permission_denied</c>): wrong restic repo password, wrong
/// cloud OAuth token, expired credentials, or 403 from the
/// backend. Retrying with the same credentials produces the same
/// failure, so the orchestrator marks it non-retriable and asks
/// the user to fix the credentials.
/// </summary>
public sealed class EngineAuthFailedException : System.Exception, INonRetriable
{
    public EngineAuthFailedException(string message) : base(message) { }
    public EngineAuthFailedException(string message, System.Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when the engine rejects the request as malformed
/// (Connect-Go code <c>invalid_argument</c>): bad storage URI,
/// unknown destination kind, malformed retention policy. These
/// are configuration mistakes — burning the 5/20/60 s retry
/// cascade against an invalid URI is pure delay.
/// </summary>
public sealed class EngineInvalidArgumentException : System.Exception, INonRetriable
{
    public EngineInvalidArgumentException(string message) : base(message) { }
    public EngineInvalidArgumentException(string message, System.Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when the engine reports that the destination is
/// unreachable in a way the user is expected to tolerate: laptop
/// offline, VPN down, NAS host unreachable, external drive
/// unplugged. The orchestrator routes this to a <c>Skipped</c>
/// cell outcome (not <c>Failed</c>) so a night away from home
/// doesn't paint the card red.
///
/// Detection is the responsibility of <see cref="EngineErrorClassifier"/>
/// — the call site catches whatever the engine actually threw and
/// re-throws this if the classifier returns TOLERABLE.
/// </summary>
public sealed class EngineUnreachableDestinationException : System.Exception, INonRetriable, ITolerable
{
    /// <summary>Short human-facing reason the cell summary surfaces
    /// (e.g. "Network share unreachable", "Drive not present").</summary>
    public string Reason { get; }

    public EngineUnreachableDestinationException(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public EngineUnreachableDestinationException(string reason, string message, System.Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }
}
