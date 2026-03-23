using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Services;
using OpenKikaiSan.App.Utils;

namespace OpenKikaiSan.App;

public partial class App
{
    private OpenXrControllerState ReinitializeOpenXr(AppLogger logger)
    {
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;

        var openXrControllerInputService = new OpenXrControllerInputService(logger);
        var initializeState = openXrControllerInputService.Initialize();
        if (initializeState.IsInitialized)
        {
            logger.Info($"OpenXR input initialize: {initializeState.Status}");
        }
        else
        {
            logger.Warn($"OpenXR input initialize failed: {initializeState.Status}");
        }

        if (initializeState.IsInitialized)
        {
            _openXrControllerInputService = openXrControllerInputService;
        }
        else
        {
            openXrControllerInputService.Dispose();
        }

        return initializeState;
    }
}
