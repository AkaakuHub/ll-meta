using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Utils;

namespace LLMeta.App;

public partial class App
{
    private OpenXrControllerState ReinitializeOpenXr(AppLogger logger)
    {
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;

        var openXrControllerInputService = new OpenXrControllerInputService();
        var initializeState = openXrControllerInputService.Initialize();
        logger.Info($"OpenXR input initialize: {initializeState.Status}");

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
