// Assets/Scripts/Networking/PacingReceiver.cs
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class PacingReceiver : MonoBehaviour
{
    [Header("Local bind (このポートを Broadcaster.clientPort と一致)")]
    public int localPort = 9001;

    [Header("Ack Sampling")]
    public int anycastSlots = 16;   // サーバと一致させる
    public string deviceId = "client-01";

    [Header("Reply to server")]
    public string serverHost = "127.0.0.1";
    public int serverPort = 9000;

    [Header("Diagnostics")]
    public bool logReceives = true;
    public bool logAcks = false;

    private UdpClient _udp;
    private IPEndPoint _serverEp;
    private uint _lastSeq;

    private void Start()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        _udp.Client.ReceiveTimeout = 1;
        DisableWindowsUdpConnReset(_udp);

        // サーバ宛て（ACK送信用）
        if (!IPAddress.TryParse(serverHost, out var ip))
        {
            var entry = Dns.GetHostEntry(serverHost);
            foreach (var a in entry.AddressList)
                if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
        }
        if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            Debug.LogWarning($"[Receiver] serverHost could not resolve to IPv4: {serverHost}");
            ip = IPAddress.Loopback;
        }
        _serverEp = new IPEndPoint(ip, serverPort);

        Debug.Log($"[Receiver] bind={localPort} ackSlots={anycastSlots} id={deviceId} -> server={_serverEp}");
    }

    private void Update()
    {
        while (_udp.Available > 0)
        {
            try
            {
                IPEndPoint ep = null;
                var buf = _udp.Receive(ref ep);
                if (buf.Length < 6) continue;

                // [Seq(4)][Slot(2)][payload...]
                uint seq = ReadU32(buf, 0);
                ushort slot = ReadU16(buf, 4);

                if (seq <= _lastSeq) continue; // 冪等：最新のみ適用
                _lastSeq = seq;

                var text = (buf.Length > 6) ? Encoding.UTF8.GetString(buf, 6, buf.Length - 6) : "";
                if (logReceives)
                    Debug.Log($"[Receiver] <- seq={seq} slot={slot} from={ep} text='{text}'");

                // 当番ならACK（WriteWithoutResponse相当の軽量ACKをUDPで模倣）
                if (IsMySlot(slot))
                    SendAck(seq);
            }
            catch (SocketException)
            {
                // 無視（WindowsのICMPなど）
                break;
            }
        }
    }

    private bool IsMySlot(ushort slot)
    {
        int h = deviceId.GetHashCode();
        int mySlot = Math.Abs(h) % Math.Max(1, anycastSlots);
        return mySlot == slot;
    }

    private void SendAck(uint seq)
    {
        var buf = new byte[6];
        buf[0] = (byte)'A'; buf[1] = (byte)'K';
        WriteU32(buf, 2, seq);
        try
        {
            _udp.Send(buf, buf.Length, _serverEp);
            if (logAcks) Debug.Log($"[Receiver] ACK -> {seq} to {_serverEp}");
        }
        catch { /* ignore */ }
    }

    private void OnDestroy() => _udp?.Close();

    // ---- utils ----
    private static void DisableWindowsUdpConnReset(UdpClient udp)
    {
        if (Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.WindowsPlayer)
        {
            const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
            try { udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null); }
            catch { /* ignore */ }
        }
    }

    private static ushort ReadU16(byte[] p, int ofs) => (ushort)((p[ofs] << 8) | p[ofs + 1]);
    private static uint ReadU32(byte[] p, int ofs) => (uint)((p[ofs] << 24) | (p[ofs + 1] << 16) | (p[ofs + 2] << 8) | p[ofs + 3]);
    private static void WriteU32(byte[] p, int ofs, uint v)
    {
        p[ofs] = (byte)(v >> 24); p[ofs + 1] = (byte)(v >> 16);
        p[ofs + 2] = (byte)(v >> 8); p[ofs + 3] = (byte)v;
    }
}
