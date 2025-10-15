using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class PacingBroadcaster : MonoBehaviour
{
    [Header("Peers (unicast fan-out)")] [Tooltip("宛先クライアントのホスト/IP（IPv4推奨）。複数可。")] [SerializeField]
    private string[] _clientHosts = { "127.0.0.1" };

    [Tooltip("クライアントの受信ポート（PacingReceiver.localPort と一致させる）")] [SerializeField]
    private int _clientPort = 9001;

    [Header("Local bind")] [Tooltip("サーバのローカル受信ポート（任意）。別に受信しないなら衝突しない値でOK")] [SerializeField]
    private int _localPort = 9000;

    [Header("Rate / Redundancy")] [Tooltip("目標送信レート (packets/sec)")] [SerializeField]
    private float _targetPps = 50f;

    [Tooltip("瞬間バースト上限（トークンバケツ容量）")] [SerializeField]
    private int _burst = 10;

    [Tooltip("同一メッセージの冗長送信回数（2〜3推奨）")] [SerializeField]
    private int _redundancy = 2;

    [Tooltip("冗長送信の間隔 (ms)")] [SerializeField]
    private int _redundancyGapMs = 20;

    [Header("AIMD (Congestion control)")] [Tooltip("安定時の加算増加量 (pps/frame換算でだいたい)")] [SerializeField]
    private float _aimdAdd = 0.5f;

    [Tooltip("問題検出時の乗算減少率 (0.5=半減)")] [SerializeField]
    private float _aimdMul = 0.5f;

    [Tooltip("レート下限")] [SerializeField] private float _minPps = 5f;

    [Tooltip("レート上限")] [SerializeField] private float _maxPps = 200f;

    [Header("Jitter")] [Tooltip("ユニキャスト送信の微小ランダム遅延 最小値 (ms)")] [SerializeField]
    private int _jitterMinMs = 2;

    [Tooltip("ユニキャスト送信の微小ランダム遅延 最大値 (ms)")] [SerializeField]
    private int _jitterMaxMs = 8;

    [Header("Ack Sampling")] [Tooltip("サンプリングACKのスロット数（大きいほどACKが少なくなる）")] [SerializeField]
    private int _anycastSlots = 16;

    [Header("Diagnostics")] [Tooltip("1秒ごとのダイアグ送信を有効化（切り分け用）")] [SerializeField]
    private bool _enableDiagPing = false;

    [Tooltip("送信成功時にログを出す")] [SerializeField]
    private bool _logSends = true;

    [Tooltip("送信例外時に警告ログを出す")] [SerializeField]
    private bool _logSendErrors = true;

    private UdpClient _udp;
    private readonly List<IPEndPoint> _peers = new List<IPEndPoint>();
    private TokenBucket _bucket;
    private uint _seq;
    private float _pps;

    // 送信用ワークキュー
    private struct PendingSend
    {
        public IPEndPoint ep;
        public byte[] bytes;
        public double when;
    }

    private readonly List<PendingSend> _queue = new List<PendingSend>();

    private float _diagTimer;

    private void Start()
    {
        // Bind（受信はほぼ使わないが、ACK 受け取りのために開けておく）
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _localPort));
        _udp.Client.ReceiveTimeout = 1;
        DisableWindowsUdpConnReset(_udp);

        // Peers（IPv4 のみに絞る）
        _peers.Clear();
        foreach (string host in _clientHosts)
        {
            if (string.IsNullOrWhiteSpace(host)) continue;

            if (IPAddress.TryParse(host, out IPAddress ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork) // IPv4のみ
                {
                    _peers.Add(new IPEndPoint(ip, _clientPort));
                }
            }
            else
            {
                try
                {
                    IPHostEntry entry = Dns.GetHostEntry(host);
                    foreach (IPAddress address in entry.AddressList)
                    {
                        if (address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            _peers.Add(new IPEndPoint(address, _clientPort));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Broadcaster] DNS resolve failed: {host} ({ex.Message})");
                }
            }
        }

        _pps = Mathf.Clamp(_targetPps, _minPps, _maxPps);
        _bucket = new TokenBucket(_pps, _burst);

        Debug.Log($"[Broadcaster] bind={_localPort}, peers={_peers.Count} (IPv4), pps={_pps}");
        for (int i = 0; i < _peers.Count; i++)
        {
            Debug.Log($"[Broadcaster] peer[{i}]={_peers[i]}");
        }
    }

    private void Update()
    {
        // 例：毎フレーム 現在状態スナップショットを送る（冪等）
        byte[] snapshot = BuildSnapshotPayload();

        // ユニキャスト・ファンアウト（順送り）+ 冗長送信
        FanOutSend(snapshot);

        // キューの送信時刻になったものを実送
        FlushQueue();

        // 軽い受信（サンプリングACK を読む）
        PollAcks();

        // 安定していそうなら少しずつPPSを増やす（AIMD: 加算的増加）
        _pps = Mathf.Min(_pps + _aimdAdd * Time.deltaTime * 60f, _maxPps);
        _bucket.SetRate(_pps);

        // 切り分け用：強制ダイアグ送信
        if (_enableDiagPing)
        {
            _diagTimer += Time.deltaTime;
            if (_diagTimer >= 1f)
            {
                _diagTimer = 0f;
                byte[] ping = Encoding.UTF8.GetBytes("diag-ping");
                foreach (IPEndPoint ep in _peers)
                {
                    try
                    {
                        _udp.Send(ping, ping.Length, ep);
                        if (_logSends)
                        {
                            Debug.Log($"[Broadcaster] diag-ping -> {ep}");
                        }
                    }
                    catch (SocketException se)
                    {
                        if (_logSendErrors)
                        {
                            Debug.LogWarning($"[Broadcaster] diag send err {se.SocketErrorCode}");
                        }
                    }
                }
            }
        }
    }

    private byte[] BuildSnapshotPayload()
    {
        // [Seq(4)][AnycastSlot(2)][UTF8 text...]
        string text = $"state={DateTime.Now:HH:mm:ss}";
        byte[] textBytes = Encoding.UTF8.GetBytes(text);
        var anycastSlot = (ushort)(_seq % (uint)Mathf.Max(1, _anycastSlots));

        byte[] buf = new byte[6 + textBytes.Length];
        WriteU32(buf, 0, _seq);
        WriteU16(buf, 4, anycastSlot);
        Array.Copy(textBytes, 0, buf, 6, textBytes.Length);
        return buf;
    }

    /// <summary>
    /// 複数に分配送信
    /// 少しずつずらして（ジッタ）、各 Peer に冗長送信
    /// </summary>
    /// <param name="payload">送信するペイロード</param>
    private void FanOutSend(byte[] payload)
    {
        // 各ピアに対し、冗長送信 r 回
        foreach (IPEndPoint ep in _peers)
        {
            for (int i = 0; i < _redundancy; i++)
            {
                double when = Now() + i * (_redundancyGapMs / 1000.0);
                double jitter = UnityEngine.Random.Range(_jitterMinMs, _jitterMaxMs) / 1000.0;
                Enqueue(ep, payload, when + jitter);
            }
        }

        _seq++; // 冪等：最新だけ意味を持つ
    }

    private void Enqueue(IPEndPoint ep, byte[] bytes, double when)
    {
        _queue.Add(new PendingSend
        {
            ep = ep,
            bytes = bytes,
            when = when,
        });
    }

    /// <summary>
    /// 規定の時間になったものを送信
    /// </summary>
    private void FlushQueue()
    {
        double now = Now();
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            PendingSend ps = _queue[i];
            if (ps.when > now) continue;

            // TokenBucket でペーシング（1 送信 = 1 トークン）
            if (!_bucket.Consume(1)) continue;

            try
            {
                _udp.Send(ps.bytes, ps.bytes.Length, ps.ep);
                if (_logSends)
                {
                    Debug.Log($"[Broadcaster] sent seq={ReadU32(ps.bytes, 0)} to {ps.ep} size={ps.bytes.Length} pps={_pps:F1}");
                }
                _queue.RemoveAt(i);
            }
            catch (SocketException se)
            {
                if (se.SocketErrorCode == SocketError.WouldBlock ||
                    se.SocketErrorCode == SocketError.TryAgain ||
                    se.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    // AIMD: 乗算的減少
                    _pps = Mathf.Max(_pps * _aimdMul, _minPps);
                    _bucket.SetRate(_pps);
                    if (_logSendErrors)
                    {
                        Debug.LogWarning($"[Broadcaster] send congested -> pps={_pps:F1} ({se.SocketErrorCode})");
                    }
                    // 失敗分はキューに残す（次フレームで再試行）
                }
                else
                {
                    if (_logSendErrors)
                    {
                        Debug.LogWarning($"[Broadcaster] send err {se.SocketErrorCode}");
                    }
                }
            }
        }
    }

    private void PollAcks()
    {
        while (_udp.Available > 0)
        {
            try
            {
                IPEndPoint ep = null;
                byte[] buf = _udp.Receive(ref ep);
                if (buf.Length < 6) continue;

                // ACK 形式：[Tag(2)='AK'][Seq(4)]
                if (buf[0] == (byte)'A' && buf[1] == (byte)'K')
                {
                    uint ackSeq = ReadU32(buf, 2);
                    // ざっくり：ACK を見れたので"増やす方向"を少し許容
                    _pps = Mathf.Min(_pps + _aimdAdd, _maxPps);
                    _bucket.SetRate(_pps);
                    // 必要ならログ
                    // Debug.Log($"[Broadcaster] ACK {ackSeq} from {ep} -> pps={_pps:F1}");
                }
            }
            catch (SocketException)
            {
                // 無視（WindowsのICMP由来など）
                break;
            }
        }
    }

    private void OnDestroy()
    {
        _udp?.Close();
    }

    // ---- utils ----
    private static double Now()
    {
#if UNITY_2021_2_OR_NEWER
        return Time.realtimeSinceStartupAsDouble;
#else
        return (double)Time.realtimeSinceStartup;
#endif
    }

    private static void DisableWindowsUdpConnReset(UdpClient udp)
    {
        if (Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.WindowsPlayer)
        {
            const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
            try
            {
                udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    private static void WriteU16(byte[] p, int ofs, ushort v)
    {
        p[ofs] = (byte)(v >> 8);
        p[ofs + 1] = (byte)v;
    }

    private static void WriteU32(byte[] p, int ofs, uint v)
    {
        p[ofs] = (byte)(v >> 24);
        p[ofs + 1] = (byte)(v >> 16);
        p[ofs + 2] = (byte)(v >> 8);
        p[ofs + 3] = (byte)v;
    }

    private static uint ReadU32(byte[] p, int ofs)
    {
        return (uint)((p[ofs] << 24) | (p[ofs + 1] << 16) | (p[ofs + 2] << 8) | p[ofs + 3]);
    }
}