namespace Jellyfin.Plugin.BookEnhancer;

/// <summary>
/// Helpers for deciding whether an <see cref="System.OperationCanceledException"/>
/// represents a genuine caller cancellation or an unrelated timeout.
/// </summary>
internal static class CancellationHelper
{
    /// <summary>
    /// Returns true when the exception was triggered by the supplied caller token.
    /// </summary>
    /// <param name="ex">The operation-canceled exception.</param>
    /// <param name="callerToken">The caller's cancellation token.</param>
    /// <returns>True if the caller requested cancellation; otherwise false.</returns>
    public static bool IsCallerCancellation(this System.OperationCanceledException ex, System.Threading.CancellationToken callerToken)
    {
        return ex.CancellationToken == callerToken && callerToken.IsCancellationRequested;
    }
}
