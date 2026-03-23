using System;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.OpenXR;

namespace OpenKikaiSan.App.Services;

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
        var result = getRequirements(instance, systemId, &graphicsRequirements);
        if (result == Result.Success)
        {
            _requiredGraphicsAdapterLuid = graphicsRequirements.AdapterLuid;
            _hasRequiredGraphicsAdapterLuid = true;
            _graphicsAdapterSummary = $"requiredLuid=0x{graphicsRequirements.AdapterLuid:X16}";
        }

        return result;
    }

    private int CreateD3D11Device()
    {
        lock (_videoFrameLock)
        {
            _selectedGraphicsBackendLabel = "D3D11";
        }

        var d3d11 = D3D11.GetApi();
        var dxgi = DXGI.GetApi();
        D3DFeatureLevel featureLevel;
        ID3D11Device* d3d11Device = null;
        ID3D11DeviceContext* d3d11DeviceContext = null;
        IDXGIFactory1* dxgiFactory = null;
        IDXGIAdapter1* selectedAdapter = null;

        if (!_hasRequiredGraphicsAdapterLuid)
        {
            return unchecked((int)0x887A0002);
        }

        try
        {
            void* factoryPointer = null;
            var factoryGuid = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
            var createFactoryResult = dxgi.CreateDXGIFactory1(ref factoryGuid, ref factoryPointer);
            if (createFactoryResult < 0 || factoryPointer is null)
            {
                return createFactoryResult;
            }

            dxgiFactory = (IDXGIFactory1*)factoryPointer;
            var targetAdapterLuid = _requiredGraphicsAdapterLuid;
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                IDXGIAdapter1* adapter = null;
                var enumResult = dxgiFactory->EnumAdapters1(adapterIndex, &adapter);
                if (enumResult == DxgiErrorNotFound)
                {
                    break;
                }

                if (enumResult < 0 || adapter is null)
                {
                    break;
                }

                AdapterDesc1 adapterDesc = default;
                if (adapter->GetDesc1(&adapterDesc) >= 0)
                {
                    var adapterLuid = ConvertLuidToUInt64(adapterDesc.AdapterLuid);
                    var adapterLabel = BuildAdapterLabel(adapterDesc, adapterLuid, adapterIndex);

                    if (adapterLuid == targetAdapterLuid)
                    {
                        selectedAdapter = adapter;
                        _graphicsAdapterSummary =
                            $"requiredLuid=0x{_requiredGraphicsAdapterLuid:X16} selectedAdapterIndex={adapterIndex}";
                        _selectedGraphicsAdapterLabel = adapterLabel;
                        break;
                    }
                }

                _ = adapter->Release();
            }
        }
        finally
        {
            if (dxgiFactory is not null)
            {
                _ = dxgiFactory->Release();
            }
        }

        if (selectedAdapter is null)
        {
            _graphicsAdapterSummary =
                $"adapter-not-found requiredLuid=0x{_requiredGraphicsAdapterLuid:X16}";
            return DxgiErrorNotFound;
        }

        var createResult = d3d11.CreateDevice(
            (IDXGIAdapter*)selectedAdapter,
            D3DDriverType.Unknown,
            IntPtr.Zero,
            (uint)(CreateDeviceFlag.BgraSupport | CreateDeviceFlag.VideoSupport),
            (D3DFeatureLevel*)0,
            0,
            (uint)D3D11.SdkVersion,
            &d3d11Device,
            &featureLevel,
            &d3d11DeviceContext
        );
        _ = selectedAdapter->Release();
        if (createResult >= 0)
        {
            var multithreadProtectResult = EnableD3D11MultithreadProtection(d3d11Device);
            if (multithreadProtectResult < 0)
            {
                _graphicsAdapterSummary =
                    $"multithread-protect-failed hr=0x{multithreadProtectResult:X8} requiredLuid=0x{_requiredGraphicsAdapterLuid:X16}";
                _ = d3d11DeviceContext->Release();
                _ = d3d11Device->Release();
                return multithreadProtectResult;
            }

            _d3d11Device = d3d11Device;
            _d3d11DeviceContext = d3d11DeviceContext;
            return createResult;
        }

        return createResult;
    }

    private int EnableD3D11MultithreadProtection(ID3D11Device* d3d11Device)
    {
        void* multithreadPointer = null;
        var multithreadGuid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        var queryResult = d3d11Device->QueryInterface(ref multithreadGuid, ref multithreadPointer);
        if (queryResult < 0 || multithreadPointer is null)
        {
            return queryResult < 0 ? queryResult : unchecked((int)0x80004005);
        }

        var multithread = (ID3D11Multithread*)multithreadPointer;
        _ = multithread->SetMultithreadProtected(1);
        var enabled = multithread->GetMultithreadProtected();
        _ = multithread->Release();
        if (!enabled)
        {
            return unchecked((int)0x80004005);
        }

        return 0;
    }

    private static ulong ConvertLuidToUInt64(Luid luid)
    {
        Span<Luid> luidSpan = stackalloc Luid[1];
        luidSpan[0] = luid;
        var bytes = MemoryMarshal.AsBytes(luidSpan);
        return BitConverter.ToUInt64(bytes);
    }

    private static string BuildAdapterLabel(
        AdapterDesc1 adapterDesc,
        ulong adapterLuid,
        uint adapterIndex
    )
    {
        _ = adapterDesc;
        return $"Adapter idx={adapterIndex} | LUID=0x{adapterLuid:X16}";
    }
}
