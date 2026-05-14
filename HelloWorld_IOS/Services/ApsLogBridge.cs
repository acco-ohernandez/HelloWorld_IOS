using NwdViewer.Aps;

namespace HelloWorld_IOS.Services;

/// <summary>
/// Forwards <see cref="IApsLogger"/> calls from the NwdViewer.Aps library into the
/// iOS app's <see cref="Logger"/> facade (which tees to Debug + SessionLogger).
/// Registered once in MauiProgram via <see cref="ApsLog.SetSink"/>.
/// </summary>
internal sealed class ApsLogBridge : IApsLogger
{
    public void Info(string category, string message)              => Logger.Info(category, message);
    public void Warn(string category, string message)              => Logger.Warn(category, message);
    public void Error(string category, string message, Exception? ex = null) => Logger.Error(category, message, ex);
}
