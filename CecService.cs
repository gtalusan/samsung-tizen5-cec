using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Tizen.Applications;

namespace CecService
{
    /* CecService: headless HDMI input-name service for Samsung Tizen 5.x TVs.
       Learns real device names ("RX-V4A", "PlayStation 4") from the CEC bus
       via /dev/micom-cec (passive listener), maps them to HDMI ports through
       physical addresses, serves the per-port label map as JSON on
       http://+:8787/ and persists learned names so relaunches start warm.
       See README.md for the full mechanism. */
    class App : ServiceApplication
    {
        private const string TRACE = "/opt/usr/home/owner/apps_rw/EdidServi5/data/e.status";
        private const int O_RDWR = 2;

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int read(int fd, byte[] buf, int count);

        [DllImport("libc")]
        private static extern int close(int fd);

        private HttpListener _listener;
        private string _json = "{}";
        private readonly object _lock = new object();
        private readonly List<string> _frames = new List<string>();
                private readonly Dictionary<int, string> _nameByAddr = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _portByAddr = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _depthByAddr = new Dictionary<int, int>();
        private const string NAMES_FILE = "/opt/usr/home/owner/apps_rw/EdidServi5/data/cec-names.txt";

        private static void Trace(string message)
        {
            try { File.AppendAllText(TRACE, message + "\n"); } catch { }
        }

        private static string Hex(byte[] b, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n && i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /* CEC: passive listener. The micom accepts writes but never
           transmits foreign frames, so we only capture the TV's own
           discovery dialogue: source switches, hotplugs and device
           power-ons make the TV ask for names, and the micom forwards
           those replies to this queue. */
        private void CecProbe()
        {
            int fd = open("/dev/micom-cec", O_RDWR);
            if (fd < 0) { Trace("cec open -> " + fd); return; }
            try
            {
                Trace("passive cec listen");
                /* Listen forever: read() parks between bursts (the driver
                   ignores O_NONBLOCK), so each new CEC frame wakes the loop.
                   Names learned later (device power-on, source switches)
                   update the JSON live. */
                for (;;)
                {
                    byte[] buf = new byte[32];
                    int n = read(fd, buf, buf.Length);
                    if (n <= 0) { Trace("cec read -> " + n); break; }
                    string hex = Hex(buf, n);
                    Trace("cec frame " + hex);
                    lock (_lock)
                    {
                        _frames.Add(hex);
                        ParseCecFrame(buf, n);
                        _json = BuildJson();
                        SaveNames();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace("cec exception " + ex.GetType().Name + ": " + ex.Message);
            }
            finally { close(fd); }
        }

        /* Frames are [0x80|len][CEC frame]. Record <Set OSD Name> 0x47 by
           initiator and <Report Physical Address> 0x84 (PA top nibble = the
           TV's HDMI port) to map device names onto ports. */
        private void ParseCecFrame(byte[] b, int n)
        {
            try
            {
                if (n < 2) return;
                int len = b[0] & 0x0F;
                if (1 + len > n) return;
                int init = (b[1] >> 4) & 0x0F;
                int op = b[2];
                if (op == 0x47 && len >= 3)
                {
                    string name = Encoding.ASCII.GetString(b, 3, len - 2).Trim();
                    if (name.Length > 0) _nameByAddr[init] = name;
                }
                else if (op == 0x84 && len >= 4)
                {
                    int port = (b[3] >> 4) & 0x0F; /* PA top nibble */
                    if (port >= 1 && port <= 4)
                    {
                        int depth = 0;
                        if ((b[3] & 0x0F) != 0) depth++;
                        if ((b[4] >> 4) != 0) depth++;
                        if ((b[4] & 0x0F) != 0) depth++;
                        _portByAddr[init] = port;
                        _depthByAddr[init] = depth;
                    }
                }
            }
            catch { }
        }

        /* Persist learned names so a relaunch starts warm. Line format:
           N<addr>|<name> and P<addr>|<port>|<depth>. */
        private void SaveNames()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _nameByAddr) sb.Append("N").Append(kv.Key).Append('|').Append(kv.Value).Append('\n');
                foreach (var kv in _portByAddr)
                {
                    int depth = _depthByAddr.ContainsKey(kv.Key) ? _depthByAddr[kv.Key] : 9;
                    sb.Append("P").Append(kv.Key).Append('|').Append(kv.Value).Append('|').Append(depth).Append('\n');
                }
                File.WriteAllText(NAMES_FILE, sb.ToString());
            }
            catch (Exception ex) { Trace("save exception " + ex.GetType().Name); }
        }

        private void LoadNames()
        {
            try
            {
                if (!File.Exists(NAMES_FILE)) return;
                foreach (var line in File.ReadAllLines(NAMES_FILE))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 2) continue;
                    int addr;
                    if (parts[0].Length < 2 || !int.TryParse(parts[0].Substring(1), out addr)) continue;
                    if (parts[0][0] == 'N') _nameByAddr[addr] = parts[1];
                    else if (parts[0][0] == 'P' && parts.Length >= 3)
                    {
                        int port, depth;
                        if (int.TryParse(parts[1], out port) && int.TryParse(parts[2], out depth))
                        {
                            _portByAddr[addr] = port;
                            _depthByAddr[addr] = depth;
                        }
                    }
                }
                Trace("loaded " + _nameByAddr.Count + " names, " + _portByAddr.Count + " ports");
            }
            catch (Exception ex) { Trace("load exception " + ex.GetType().Name); }
        }

        private string BuildJson()
        {
            /* Port names: prefer the CEC name of the shallowest device on
               the port (the direct device, e.g. the AVR at 3.0.0.0 over a
               PS4 behind it at 3.3.0.0); fall back to the EDID name. */
            string[] portName = new string[4]; /* null until a CEC name is learned */
            int[] portDepth = { 9, 9, 9, 9 };
            foreach (var kv in _portByAddr)
            {
                int idx = kv.Value - 1;
                if (idx < 0 || idx > 3 || !_nameByAddr.ContainsKey(kv.Key)) continue;
                int depth = _depthByAddr.ContainsKey(kv.Key) ? _depthByAddr[kv.Key] : 9;
                if (depth <= portDepth[idx]) { portDepth[idx] = depth; portName[idx] = _nameByAddr[kv.Key]; }
            }
            var sb = new StringBuilder("{");
            for (int i = 0; i < 4; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("\"hdmi" + (i + 1) + "\":" + JsonEscape(portName[i]));
            }
            sb.Append(",\"devices\":{");
            bool first = true;
            foreach (var kv in _nameByAddr)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("\"a" + kv.Key + "\":{\"name\":" + JsonEscape(kv.Value) +
                          (_portByAddr.ContainsKey(kv.Key) ? ",\"port\":" + _portByAddr[kv.Key] : "") + "}");
            }
            sb.Append('}');
            sb.Append(",\"cec\":[");
            for (int i = 0; i < _frames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonEscape(_frames[i]));
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        private void ProbeSteps()
        {
            Trace("main entered");
            lock (_lock) { LoadNames(); _json = BuildJson(); }

            /* HTTP first - never let a blocked CEC read hide the server.
               Retry: a lingering old instance may hold 8787 briefly after a
               reinstall. */
            for (int attempt = 0; attempt < 3 && _listener == null; attempt++)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add("http://+:8787/");
                    l.Start();
                    _listener = l;
                    _listener.BeginGetContext(HandleRequest, null);
                    Trace("listening on 8787");
                }
                catch (Exception ex)
                {
                    Trace("listener exception " + ex.GetType().Name + ": " + ex.Message);
                    Thread.Sleep(2000);
                }
            }

            /* CEC on its own thread; may park in a blocking read - fine. */
            var cecThread = new Thread(CecProbe);
            cecThread.IsBackground = true;
            cecThread.Start();
        }

        /* Re-arm BEFORE writing the response; a null callback here was the
           stall: request 1 answered, every later request hung forever. */
        private void HandleRequest(IAsyncResult ar)
        {
            try
            {
                HttpListenerContext ctx = _listener.EndGetContext(ar);
                _listener.BeginGetContext(HandleRequest, null);
                string payloadText;
                lock (_lock) { payloadText = _json; }
                byte[] payload = Encoding.UTF8.GetBytes(payloadText);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = payload.Length;
                ctx.Response.OutputStream.Write(payload, 0, payload.Length);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Trace("request exception " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            Trace("service create");
        }

        static void Main(string[] args)
        {
            var app = new App();
            var probeThread = new Thread(app.ProbeSteps);
            probeThread.IsBackground = true;
            probeThread.Start();
            try
            {
                app.Run(args);
            }
            catch (Exception ex)
            {
                Trace("main exception " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
