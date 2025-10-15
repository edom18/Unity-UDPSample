using System;
using System.Text;
using UnityEngine;

public class RudpClientBehaviour : MonoBehaviour
{
    [Header("Server Peer")]
    public string serverHost = "127.0.0.1";
    public int serverPort = 9000;

    [Header("Client Settings")]
    public int listenPort = 9001;

    private MiniRudp _rudp;
    private float _timer;

    private enum MsgType : byte
    {
        Ping = 1,
        Text = 2
    }

    void Start()
    {
        // クライアント: local=9001 でバインド、送信先は serverHost:serverPort
        _rudp = new MiniRudp(serverHost, serverPort, localPort: listenPort);
        Debug.Log($"[Client] Start listen={listenPort}, server={serverHost}:{serverPort}");

        // 起動直後に1回テキストを投げる
        _rudp.Send((byte)MsgType.Text, Encoding.UTF8.GetBytes("Hello from Client"));
        // ついでにPING
        _rudp.Send((byte)MsgType.Ping, ReadOnlySpan<byte>.Empty);
    }

    void Update()
    {
        // 受信ポーリング
        _rudp.PollReceive((type, payload) =>
        {
            switch ((MsgType)type)
            {
                case MsgType.Text:
                    string text = Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);
                    Debug.Log($"[Client] Text from server: {text}");
                    break;

                default:
                    Debug.Log($"[Client] Unknown msgType={type} len={payload.Count}");
                    break;
            }
        });

        // 3秒ごとにPING
        _timer += Time.deltaTime;
        if (_timer >= 3f)
        {
            _timer = 0f;
            _rudp.Send((byte)MsgType.Ping, ReadOnlySpan<byte>.Empty);
            // ついでに現在時刻をテキスト送信
            _rudp.Send((byte)MsgType.Text, Encoding.UTF8.GetBytes("Now: " + DateTime.Now.ToString("T")));
        }
    }

    void OnDestroy()
    {
        _rudp?.Close();
    }
}