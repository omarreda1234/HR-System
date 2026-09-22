using System;
using System.Runtime.InteropServices;

namespace HRSystem.Services
{
    public static class DahuaNetSDK
    {
        private const string LIBRARY_NAME = "dhnetsdk.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetDllDirectory(string lpPathName);

        // Callback delegates
        public delegate void fDisConnect(IntPtr lLoginID, string pchDVRIP, int nDVRPort, IntPtr dwUser);

        // API Methods
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern bool CLIENT_Init(fDisConnect cbDisConnect, IntPtr dwUser);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern void CLIENT_Cleanup();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr CLIENT_LoginEx2(
            string pchDVRIP,
            ushort wDVRPort,
            string pchUserName,
            string pchPassword,
            int nSpecCap,
            IntPtr pCapParam,
            ref NET_DEVICEINFO_Ex pstDeviceInfo,
            ref int pnError
        );

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern bool CLIENT_Logout(IntPtr lLoginID);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern bool CLIENT_GetNewDevConfig(
            IntPtr lLoginID,
            string szCommand,
            int nChannel,
            IntPtr szOutBuffer,
            int dwOutBufferSize,
            ref int error,
            int waittime
        );

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern int CLIENT_GetLastError();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern bool CLIENT_QueryDeviceTime(IntPtr lLoginID, ref NET_TIME pTime, int nWaitTime);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.StdCall)]
        public static extern bool CLIENT_SetupDeviceTime(IntPtr lLoginID, ref NET_TIME pTime);

        // Structures
        [StructLayout(LayoutKind.Sequential)]
        public struct NET_TIME
        {
            public int dwYear;
            public int dwMonth;
            public int dwDay;
            public int dwHour;
            public int dwMinute;
            public int dwSecond;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct NET_DEVICEINFO_Ex
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] sSerialNumber;
            public int nAlarmInPortNum;
            public int nAlarmOutPortNum;
            public int nDiskNum;
            public int nDVRType;
            public int nChanNum;
            public byte byLimitLoginTime;
            public byte byLeftLogTimes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public byte[] bReserved;
            public int nLockLeftTime;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
            public byte[] Reserved;
        }
    }
}
