using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BattleArena
{
    /// <summary>
    /// 클라이언트 한 명과의 연결 담당
    ///
    /// 접속자 1명당 이 객체가 1개 만들어집니다.
    /// </summary>
    public class ClientSession
    {
        public int PlayerId { get; private set; }
        public string Nickname { get; private set; } = "Unknown";

        // ── 위치 정보 (UDP로 갱신됨) ──
        public int X { get; set; }
        public int Y { get; set; }

        /// <summary>이 플레이어의 UDP 주소. 첫 MOVE 패킷을 받을 때 채워집니다.</summary>
        public EndPoint? UdpEndPoint { get; set; }

        /// <summary>마지막으로 적용한 시퀀스 번호</summary>
        public int LastSeq { get; set; }

        /// <summary>[수정] WELCOME 을 보낸 뒤에야 true. 그 전에는 방송 대상이 아니다.</summary>
        public bool IsLoggedIn { get; private set; }

        private readonly Socket _socket;
        private bool _isConnected = true;

        public ClientSession(Socket socket, int playerId)
        {
            _socket = socket;
            PlayerId = playerId;
        }

        // ═══════════════════════════════════════
        // 수신 루프
        // ═══════════════════════════════════════
        public async Task RunAsync()
        {
            try
            {
                while (_isConnected)
                {
                    string? json = await PacketHelper.ReceiveAsync(_socket);
                    if (json == null) break;

                    await HandlePacketAsync(json);   // [수정] async void → await
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[에러] ID:{PlayerId}  {ex.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        // ═══════════════════════════════════════
        // 패킷 처리
        //
        // 수신 루프를 막지 않기 위해 비동기로 처리합니다.
        // ═══════════════════════════════════════
        // [수정] async void 는 안에서 난 예외를 잡을 곳이 없어 프로세스가 통째로 죽는다.
        //        → async Task 로 바꿔 RunAsync 에서 await, 깨진 패킷은 이 연결만 무시.
        private async Task HandlePacketAsync(string json)
        {
            int type;
            try
            {
                Newtonsoft.Json.Linq.JObject? packet =
                    JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                int? t = packet?.Value<int?>("Type");
                if (t == null)
                {
                    Console.WriteLine($"[경고] ID:{PlayerId} Type 없는 패킷 무시");
                    return;
                }
                type = t.Value;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[경고] ID:{PlayerId} 깨진 JSON 무시 : {ex.Message}");
                return;
            }

            switch (type)
            {
                case PacketType.LOGIN:
                    await HandleLoginAsync(json);
                    break;

                case PacketType.CHAT:
                    await HandleChatAsync(json);
                    break;

                default:
                    Console.WriteLine($"[경고] 모르는 패킷 타입 : {type}");
                    break;
            }
        }

        private async Task HandleLoginAsync(string json)
        {
            if (IsLoggedIn) return;                        // [수정] 중복 로그인 무시

            LoginPacket? login = JsonConvert.DeserializeObject<LoginPacket>(json);
            if (login == null) return;

            // [수정] 닉네임은 UDP STATE 문자열에 그대로 들어간다.
            //        '|' ',' 가 섞이면 STATE 가 깨지고 가짜 플레이어를 끼워 넣을 수 있다.
            //        구분자를 지우고 길이도 제한한다 (STATE 크기 폭증 방지).
            string nick = (login.Nickname ?? "").Replace("|", "").Replace(",", "").Trim();
            if (nick.Length > 16) nick = nick.Substring(0, 16);

            Nickname = string.IsNullOrWhiteSpace(nick)
                ? $"Player{PlayerId}"
                : nick;

            await SendAsync(new WelcomePacket
            {
                PlayerId = PlayerId,
                Message = $"{Nickname}님, 배틀아레나에 오신 것을 환영합니다!"
            });

            IsLoggedIn = true;                              // [수정] WELCOME 이 나간 뒤부터 방송 대상

            GameServer.Instance.Broadcast(new NoticePacket
            {
                Type = PacketType.PLAYER_IN,
                Message = $"{Nickname}님이 입장했습니다."
            }, exceptPlayerId: PlayerId);
        }

        private async Task HandleChatAsync(string json)
        {
            if (!IsLoggedIn) return;                       // [수정] 로그인 전 채팅 무시

            ChatPacket? chat = JsonConvert.DeserializeObject<ChatPacket>(json);
            if (chat == null) return;

            // 받은 그대로 전체에게 중계합니다
            GameServer.Instance.Broadcast(new ChatEchoPacket
            {
                Nickname = Nickname,
                Message = chat.Message
            });

            await Task.CompletedTask;
        }

        // ═══════════════════════════════════════
        // 전송
        // ═══════════════════════════════════════
        public async Task SendAsync(object packet)
        {
            if (!_isConnected) return;

            try
            {
                await PacketHelper.SendAsync(_socket, packet);
            }
            catch
            {
                Disconnect();
            }
        }

        // ═══════════════════════════════════════
        // 종료
        // ═══════════════════════════════════════
        public void Disconnect()
        {
            if (!_isConnected) return;
            _isConnected = false;

            try
            {
                _socket.Shutdown(SocketShutdown.Both);
                _socket.Close();
            }
            catch { }

            GameServer.Instance.RemoveSession(PlayerId);
        }
    }
}
