using System.Text;
using UnityEngine;

public class RudpServerBehaviour : MonoBehaviour
{
    [Header("Server Settings")]
    public int listenPort = 9000;

    [Header("Client Peer (for replies)")]
    public string clientHost = "127.0.0.1";
    public int clientPort = 9001;

    private MiniRudp _rudp;

    private enum MsgType : byte
    {
        Ping = 1,
        Text = 2
    }

    void Start()
    {
        // サーバ: local=9000 でバインド、返信先(初期値)は clientHost:clientPort
        _rudp = new MiniRudp(clientHost, clientPort, localPort: listenPort);
        Debug.Log($"[Server] Start listen={listenPort}, default remote={clientHost}:{clientPort}");
    }

    void Update()
    {
        // 受信ポーリング
        _rudp.PollReceive((type, payload) =>
        {
            switch ((MsgType)type)
            {
                case MsgType.Ping:
                    Debug.Log($"[Server] Ping({payload.Count}B) from client. Reply PONG.");
                    _rudp.Send((byte)MsgType.Text, Encoding.UTF8.GetBytes("PONG"));
                    break;

                case MsgType.Text:
                    string text = Encoding.UTF8.GetString(payload.Array, payload.Offset, payload.Count);
                    Debug.Log($"[Server] Text: {text}");
                    // Echo back
                    _rudp.Send((byte)MsgType.Text, Encoding.UTF8.GetBytes("[Echo] " + text));
                    break;

                default:
                    Debug.Log($"[Server] Unknown msgType={type} len={payload.Count}");
                    break;
            }
        });
    }

    void OnDestroy()
    {
        _rudp?.Close();
    }
}