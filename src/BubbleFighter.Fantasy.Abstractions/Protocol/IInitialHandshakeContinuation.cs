namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>
/// Allows an explicitly permitted initial-packet handler to continue to ordinary login after
/// validating its own application handshake. Available only during that packet's dispatch;
/// it neither verifies the client version nor authenticates an account or a migration.
/// </summary>
public interface IInitialHandshakeContinuation
{
    /// <summary>
    /// Requests admission after the current handler returns successfully. The request is single-use
    /// and expires when dispatch ends; repeated or retained calls throw InvalidOperationException.
    /// Normal account authentication is still required on this connection.
    /// </summary>
    void ContinueWithoutVersionVerification();
}
