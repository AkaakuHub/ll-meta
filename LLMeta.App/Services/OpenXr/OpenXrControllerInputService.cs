using LLMeta.App.Models;
using LLMeta.App.Utils;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService : IDisposable
{
    private XR? _xr;
    private Instance _instance;
    private ulong _systemId = XR.NullSystemID;
    private Session _session;
    private ActionSet _actionSet;
    private XrAction _leftStickAction;
    private XrAction _rightStickAction;
    private XrAction _leftXAction;
    private XrAction _leftYAction;
    private XrAction _rightAAction;
    private XrAction _rightBAction;
    private XrAction _leftTriggerAction;
    private XrAction _leftGripAction;
    private XrAction _rightTriggerAction;
    private XrAction _rightGripAction;
    private XrAction _leftStickClickAction;
    private XrAction _rightStickClickAction;
    private Space _localSpace;
    private Space _viewSpace;
    private long _predictedDisplayTime;
    private ID3D11Device* _d3d11Device;
    private ID3D11DeviceContext* _d3d11DeviceContext;
    private SessionState _sessionState = SessionState.Unknown;
    private bool _isSessionRunning;
    private bool _isInitialized;
    private string _bindingSupportSummary = string.Empty;
    private long _lastInputTelemetryLogUnixMs;
    private long _lastSbsSplitTelemetryLogUnixMs;
    private readonly AppLogger? _logger;

    public OpenXrControllerInputService(
        string preferredSwapchainFormat = "Auto",
        string preferredGraphicsAdapter = "Auto",
        string preferredGraphicsBackend = "D3D11",
        AppLogger? logger = null
    )
    {
        _requestedSwapchainFormatLabel = NormalizePreferredSwapchainFormat(
            preferredSwapchainFormat
        );
        _requestedGraphicsAdapterLabel = NormalizePreferredGraphicsAdapter(
            preferredGraphicsAdapter
        );
        _requestedGraphicsBackendLabel = NormalizePreferredGraphicsBackend(
            preferredGraphicsBackend
        );
        _logger = logger;
    }

    public void SetLatestDecodedSbsFrame(DecodedVideoFrame frame)
    {
        UpdateLatestSbsFrame(frame);
    }

    public OpenXrVideoRenderConfigState GetVideoRenderConfigStateSnapshot()
    {
        lock (_videoFrameLock)
        {
            return new OpenXrVideoRenderConfigState(
                _requestedSwapchainFormatLabel,
                _selectedSwapchainFormatLabel,
                _availableSwapchainFormatLabels.ToArray(),
                _requestedGraphicsAdapterLabel,
                _selectedGraphicsAdapterLabel,
                _availableGraphicsAdapterLabels.ToArray(),
                _requestedGraphicsBackendLabel,
                _selectedGraphicsBackendLabel,
                _availableGraphicsBackends.ToArray(),
                _videoProcessorProbeSummary
            );
        }
    }

    public nint GetD3D11DevicePointer()
    {
        return (nint)_d3d11Device;
    }

    private OpenXrControllerState CreateState(string status)
    {
        return new OpenXrControllerState(
            _isInitialized,
            status,
            new OpenXrHeadPoseState(
                false,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0
            ),
            0.064f,
            90.0f,
            -0.7853982f,
            0.7853982f,
            0.7853982f,
            -0.7853982f,
            -0.7853982f,
            0.7853982f,
            0.7853982f,
            -0.7853982f,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            false,
            false,
            false,
            false,
            false
        );
    }
}
