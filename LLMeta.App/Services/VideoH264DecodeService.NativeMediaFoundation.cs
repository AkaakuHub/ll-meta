using System.Runtime.InteropServices;

namespace LLMeta.App.Services;

public sealed partial class VideoH264DecodeService
{
    private static class NativeMediaFoundation
    {
        private const int MfVersion = 0x00020070;

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        public static void MFStartupFull()
        {
            var hr = MFStartup(MfVersion, 0);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        public static void MFShutdownChecked()
        {
            var hr = MFShutdown();
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
    }
}
