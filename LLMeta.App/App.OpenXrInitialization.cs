using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Utils;

namespace LLMeta.App;

public partial class App
{
    private OpenXrControllerState ReinitializeOpenXr(
        AppLogger logger,
        string preferredSwapchainFormat,
        string preferredGraphicsAdapter,
        string preferredGraphicsBackend
    )
    {
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;

        var openXrControllerInputService = new OpenXrControllerInputService(
            preferredSwapchainFormat,
            preferredGraphicsAdapter,
            preferredGraphicsBackend,
            logger
        );
        var initializeState = openXrControllerInputService.Initialize();
        logger.Info($"OpenXR input initialize: {initializeState.Status}");

        if (initializeState.IsInitialized)
        {
            _openXrControllerInputService = openXrControllerInputService;
            if (_videoH264DecodeService is not null)
            {
                _videoH264DecodeService.SetD3D11DevicePointer(
                    openXrControllerInputService.GetD3D11DevicePointer()
                );
            }
        }
        else
        {
            openXrControllerInputService.Dispose();
        }

        return initializeState;
    }
}
