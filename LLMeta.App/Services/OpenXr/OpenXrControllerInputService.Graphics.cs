using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result GetD3D11GraphicsRequirements(Instance instance, ulong systemId)
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        PfnVoidFunction getRequirementsProc = default;
        var procResult = _xr.GetInstanceProcAddr(
            instance,
            "xrGetD3D11GraphicsRequirementsKHR",
            ref getRequirementsProc
        );
        if (procResult != Result.Success)
        {
            return procResult;
        }

        var procPointer = (nint)getRequirementsProc;
        if (procPointer == 0)
        {
            return Result.ErrorFunctionUnsupported;
        }

        var graphicsRequirements = new GraphicsRequirementsD3D11KHR
        {
            Type = StructureType.GraphicsRequirementsD3D11Khr,
        };
        var getRequirements = (delegate* unmanaged[Stdcall]<
            Instance,
            ulong,
            GraphicsRequirementsD3D11KHR*,
            Result>)procPointer;
        return getRequirements(instance, systemId, &graphicsRequirements);
    }

    private int CreateD3D11Device()
    {
        var d3d11 = D3D11.GetApi();
        D3DFeatureLevel featureLevel;
        ID3D11Device* d3d11Device = null;
        ID3D11DeviceContext* d3d11DeviceContext = null;
        var createResult = d3d11.CreateDevice(
            (IDXGIAdapter*)0,
            D3DDriverType.Hardware,
            IntPtr.Zero,
            0,
            (D3DFeatureLevel*)0,
            0,
            (uint)D3D11.SdkVersion,
            &d3d11Device,
            &featureLevel,
            &d3d11DeviceContext
        );
        if (createResult >= 0)
        {
            _d3d11Device = d3d11Device;
            _d3d11DeviceContext = d3d11DeviceContext;
            return createResult;
        }

        createResult = d3d11.CreateDevice(
            (IDXGIAdapter*)0,
            D3DDriverType.Warp,
            IntPtr.Zero,
            0,
            (D3DFeatureLevel*)0,
            0,
            (uint)D3D11.SdkVersion,
            &d3d11Device,
            &featureLevel,
            &d3d11DeviceContext
        );
        if (createResult >= 0)
        {
            _d3d11Device = d3d11Device;
            _d3d11DeviceContext = d3d11DeviceContext;
        }

        return createResult;
    }
}
