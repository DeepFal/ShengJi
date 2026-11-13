using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShengJi.Core;
using ShengJi.Interop;

namespace ShengJi.Audio
{
    internal sealed class DeviceInfo
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    internal sealed class AppInfo
    {
        /// <summary>小写 exe 文件名，例如 "msedge.exe"，作为应用的稳定标识。</summary>
        public string ExeName;
        public string DisplayName;
        public string Path;
        public ImageSource Icon;   // 已 Freeze，可跨线程使用
        public bool IsPlaying;     // 有处于活动状态的音频会话
    }

    internal sealed class SourceCatalog
    {
        public List<DeviceInfo> Outputs = new List<DeviceInfo>();
        public List<DeviceInfo> Inputs = new List<DeviceInfo>();
        public List<AppInfo> Apps = new List<AppInfo>();
        public bool AppCaptureSupported;
    }

    internal static class DeviceCatalog
    {
        /// <summary>枚举设备与正在使用音频的应用。会调用 COM，请在后台线程（MTA）调用。</summary>
        public static SourceCatalog Build()
        {
            var cat = new SourceCatalog { AppCaptureSupported = Native.SupportsProcessLoopback };
            IMMDeviceEnumerator en = null;
            try
            {
                en = CoreAudioUtil.CreateEnumerator();
                string defOut = GetDefaultId(en, EDataFlow.Render, ERole.Console);
                string defIn = GetDefaultId(en, EDataFlow.Capture, ERole.Console);
                cat.Outputs = ListDevices(en, EDataFlow.Render, defOut);
                cat.Inputs = ListDevices(en, EDataFlow.Capture, defIn);
                if (cat.AppCaptureSupported)
                    cat.Apps = ListApps(en);
            }
            catch (Exception ex) { Log.Error("枚举设备失败", ex); }
            finally { CoreAudioUtil.Release(en); }
            return cat;
        }

        public static string GetDefaultId(IMMDeviceEnumerator en, EDataFlow flow, ERole role)
        {
            try { return GetDefaultIdStrict(en, flow, role); }
            catch { return null; }
        }

        /// <summary>严格版：没有默认设备返回 null；查询本身出错（服务异常等）则抛出异常。</summary>
        public static string GetDefaultIdStrict(IMMDeviceEnumerator en, EDataFlow flow, ERole role)
        {
            IMMDevice d = null;
            try
            {
                int hr = en.GetDefaultAudioEndpoint(flow, role, out d);
                if (hr == CoreAudioConst.E_NOTFOUND || (hr >= 0 && d == null)) return null;
                if (hr < 0) throw new COMException($"GetDefaultAudioEndpoint 失败 (0x{hr:X8})", hr);
                return CoreAudioUtil.GetId(d);
            }
            finally { CoreAudioUtil.Release(d); }
        }

        /// <summary>严格版：设备不存在/未激活返回 false；查询出错则抛出异常（避免把服务故障误判成“设备断开”）。</summary>
        public static bool IsDeviceActiveStrict(IMMDeviceEnumerator en, string id)
        {
            if (id == null) return false;
            IMMDevice d = null;
            try
            {
                int hr = en.GetDevice(id, out d);
                if (hr == CoreAudioConst.E_NOTFOUND || (hr >= 0 && d == null)) return false;
                if (hr < 0) throw new COMException($"GetDevice 失败 (0x{hr:X8})", hr);
                hr = d.GetState(out DeviceState s);
                if (hr < 0) throw new COMException($"GetState 失败 (0x{hr:X8})", hr);
                return s == DeviceState.Active;
            }
            finally { CoreAudioUtil.Release(d); }
        }

        public static List<DeviceInfo> ListDevices(IMMDeviceEnumerator en, EDataFlow flow, string defaultId, bool strict = false)
        {
            var list = new List<DeviceInfo>();
            IMMDeviceCollection col = null;
            try
            {
                int hr = en.EnumAudioEndpoints(flow, DeviceState.Active, out col);
                if (hr < 0 || col == null)
                {
                    if (strict) throw new COMException($"EnumAudioEndpoints 失败 (0x{hr:X8})", hr);
                    return list;
                }
                col.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d = null;
                    try
                    {
                        if (col.Item(i, out d) != 0 || d == null) continue;
                        string id = CoreAudioUtil.GetId(d);
                        if (id == null) continue;
                        list.Add(new DeviceInfo
                        {
                            Id = id,
                            Name = CoreAudioUtil.GetFriendlyName(d) ?? "未知设备",
                            IsDefault = id == defaultId,
                        });
                    }
                    finally { CoreAudioUtil.Release(d); }
                }
            }
            finally { CoreAudioUtil.Release(col); }
            return list;
        }

        public static string GetDeviceName(IMMDeviceEnumerator en, string id)
        {
            IMMDevice d = null;
            try
            {
                if (id == null || en.GetDevice(id, out d) != 0 || d == null) return null;
                return CoreAudioUtil.GetFriendlyName(d);
            }
            catch { return null; }
            finally { CoreAudioUtil.Release(d); }
        }

        public static bool IsDeviceActive(IMMDeviceEnumerator en, string id)
        {
            IMMDevice d = null;
            try
            {
                if (id == null || en.GetDevice(id, out d) != 0 || d == null) return false;
                return d.GetState(out DeviceState s) == 0 && s == DeviceState.Active;
            }
            catch { return false; }
            finally { CoreAudioUtil.Release(d); }
        }

        /// <summary>列出所有输出设备上有音频会话的应用（按 exe 去重）。</summary>
        private static List<AppInfo> ListApps(IMMDeviceEnumerator en)
        {
            var byExe = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            var procs = ProcessTable.Snapshot();

            IMMDeviceCollection col = null;
            try
            {
                if (en.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out col) != 0 || col == null) return new List<AppInfo>();
                col.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d = null;
                    IAudioSessionManager2 mgr = null;
                    IAudioSessionEnumerator sessions = null;
                    try
                    {
                        if (col.Item(i, out d) != 0 || d == null) continue;
                        var iid = CoreAudioConst.IID_IAudioSessionManager2;
                        if (d.Activate(ref iid, CoreAudioConst.CLSCTX_ALL, IntPtr.Zero, out object o) != 0) continue;
                        mgr = (IAudioSessionManager2)o;
                        if (mgr.GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
                        sessions.GetCount(out int sc);
                        for (int s = 0; s < sc; s++)
                        {
                            IAudioSessionControl2 ctl = null;
                            try
                            {
                                if (sessions.GetSession(s, out ctl) != 0 || ctl == null) continue;
                                if (ctl.IsSystemSoundsSession() == 0) continue;
                                if (ctl.GetProcessId(out int pid) != 0 || pid == 0 || pid == self) continue;
                                ctl.GetState(out AudioSessionState st);
                                if (st == AudioSessionState.Expired) continue;
                                if (!procs.TryGetValue(pid, out var p)) continue;

                                // 归到同名进程树的根上（例如 Electron 应用的多个子进程）
                                string exe = p.Exe;
                                if (!byExe.TryGetValue(exe, out var app))
                                {
                                    app = new AppInfo { ExeName = exe.ToLowerInvariant() };
                                    byExe[exe] = app;
                                }
                                if (st == AudioSessionState.Active) app.IsPlaying = true;
                                if (app.Path == null) app.Path = Native.GetProcessImagePath(pid);
                            }
                            catch { }
                            finally { CoreAudioUtil.Release(ctl); }
                        }
                    }
                    catch (Exception ex) { Log.Warn("枚举音频会话失败: " + ex.Message); }
                    finally
                    {
                        CoreAudioUtil.Release(sessions);
                        CoreAudioUtil.Release(mgr);
                        CoreAudioUtil.Release(d);
                    }
                }
            }
            finally { CoreAudioUtil.Release(col); }

            foreach (var app in byExe.Values)
            {
                app.DisplayName = AppDisplay.GetName(app.Path, app.ExeName);
                app.Icon = AppDisplay.GetIcon(app.Path);
            }
            return byExe.Values
                .OrderByDescending(a => a.IsPlaying)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <summary>进程快照（Toolhelp32），带父进程关系。</summary>
    internal static class ProcessTable
    {
        public sealed class Entry
        {
            public int Pid;
            public int ParentPid;
            public string Exe;
        }

        public static Dictionary<int, Entry> Snapshot()
        {
            var map = new Dictionary<int, Entry>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == Native.INVALID_HANDLE_VALUE || snap == IntPtr.Zero) return map;
            try
            {
                var e = new Native.PROCESSENTRY32W { dwSize = Marshal.SizeOf(typeof(Native.PROCESSENTRY32W)) };
                if (!Native.Process32FirstW(snap, ref e)) return map;
                do
                {
                    map[e.th32ProcessID] = new Entry { Pid = e.th32ProcessID, ParentPid = e.th32ParentProcessID, Exe = e.szExeFile ?? "" };
                } while (Native.Process32NextW(snap, ref e));
            }
            finally { Native.CloseHandle(snap); }
            return map;
        }

        /// <summary>
        /// 找到某个 exe 的所有“根进程”：自身是该 exe，但父进程不是该 exe。
        /// 对根进程使用“包含进程树”模式录制，就能覆盖它的全部子进程。
        /// </summary>
        public static List<int> FindRoots(Dictionary<int, Entry> snap, string exeName)
        {
            var roots = new List<int>();
            foreach (var e in snap.Values)
            {
                if (!string.Equals(e.Exe, exeName, StringComparison.OrdinalIgnoreCase)) continue;
                bool parentSame = e.ParentPid != e.Pid
                    && snap.TryGetValue(e.ParentPid, out var parent)
                    && string.Equals(parent.Exe, exeName, StringComparison.OrdinalIgnoreCase);
                if (!parentSame) roots.Add(e.Pid);
            }
            roots.Sort();
            return roots;
        }
    }

    internal static class AppDisplay
    {
        public static string GetName(string path, string exeName)
        {
            string fallback = Path.GetFileNameWithoutExtension(exeName ?? "");
            if (string.IsNullOrEmpty(path)) return fallback;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                string d = (vi.FileDescription ?? "").Trim();
                if (d.Length == 0 || d.Length > 40) d = (vi.ProductName ?? "").Trim();
                if (d.Length == 0 || d.Length > 40) d = fallback;
                return d;
            }
            catch { return fallback; }
        }

        public static ImageSource GetIcon(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (ico == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch { return null; }
        }
    }
}
