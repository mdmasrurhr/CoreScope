using System;
using System.Threading.Tasks;
using CoreScope.Core;
using Windows.ApplicationModel;

namespace CoreScope.Platform;

/// <summary>
/// "Start with Windows" for Microsoft Store (MSIX) installs, via the package's StartupTask
/// (declared in AppxManifest.xml as TaskId "CoreScopeStartup"). Store apps must not use scheduled tasks or Run keys.
/// </summary>
public static class PackagedStartup
{
    private const string TaskId = "CoreScopeStartup";

    public static void Attach()
    {
        StartupRegistration.PackagedQuery = () => Task.Run(async () =>
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }).GetAwaiter().GetResult();

        StartupRegistration.PackagedSet = enable => Task.Run(async () =>
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (!enable)
                {
                    task.Disable();
                    return true;
                }
                var state = await task.RequestEnableAsync();
                if (state is StartupTaskState.DisabledByUser)
                    Log.Info("Startup task was turned off in Task Manager / Settings — Windows won't let apps re-enable it");
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch (Exception ex)
            {
                Log.Error("Packaged startup task", ex);
                return false;
            }
        }).GetAwaiter().GetResult();
    }
}
