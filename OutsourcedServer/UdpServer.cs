using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BattleArena
{
    /// <summary>
    /// UDP 위치 동기화 서버
    ///
    /// 받기 : MOVE|플레이어ID|시퀀스|X|Y
    /// 주기 : STATE|id,닉네임,x,y|id,닉네임,x,y|...
    ///
    /// TCP와 달리 접속자 전체가 소켓 하나를 공유합니다.
    /// 그래서 패킷 안의 PlayerId로 누가 보냈는지 구분합니다.
    /// </summary>
    public class UdpServer
    {
        /// <summary>초당 30번 보내기 위한 간격</summary>
        private const int TICK_MS = 33;

        private Socket _socket = null!;

        private readonly StringBuilder _stateBuilder = new StringBuilder(2048);

        private int _appliedCount;
        private int _droppedCount;

        /// <summary>위치를 실제로 갱신한 횟수</summary>
        public int AppliedCount => _appliedCount;

        /// <summary>오래된·중복 패킷이라 버린 횟수</summary>
        public int DroppedCount => _droppedCount;

        // ═══════════════════════════════════════
        // 시작
        // ═══════════════════════════════════════
        public async Task StartAsync()
        {
            _socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);

            _socket.Bind(new IPEndPoint(IPAddress.Any, StudentConfig.UdpPort));

            // Windows에서 클라이언트가 사라지면 ICMP가 돌아와
            // 다음 ReceiveFrom이 10054로 터지는 것을 막습니다.
            try
            {
                _socket.IOControl((IOControlCode)(-1744830452),
                                  new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { }

            Console.WriteLine($"[UDP] 포트 {StudentConfig.UdpPort}번에서 위치 패킷을 기다립니다");
            Console.WriteLine();

            _ = ReceiveLoopAsync();
            await BroadcastLoopAsync();
        }

        // ═══════════════════════════════════════
        // 수신 루프
        // ═══════════════════════════════════════
        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[1024];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                try
                {
                    var result = await _socket.ReceiveFromAsync(
                        new ArraySegment<byte>(buffer), SocketFlags.None, from);

                    // [수정] 버퍼 전체가 아니라 '이번에 받은 바이트 수' 만큼만 문자열로
                    string msg = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);

                    HandlePacket(msg, result.RemoteEndPoint);
                }
                catch (SocketException se)
                    when (se.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // 클라이언트 하나가 꺼진 것일 뿐입니다.
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UDP 수신 에러] {ex.Message}");
                    await Task.Delay(100);
                }
            }
        }

        // ═══════════════════════════════════════
        // 패킷 처리
        //
        // 형식 : MOVE|플레이어ID|시퀀스|X|Y
        // 예시 : MOVE|3|152|10|7
        // ═══════════════════════════════════════
        private void HandlePacket(string msg, EndPoint from)
        {
            string[] parts = msg.Split('|');
            if (parts.Length < 5) return;
            if (parts[0] != UdpPacket.MOVE) return;

            if (!int.TryParse(parts[1], out int playerId)) return;
            if (!int.TryParse(parts[2], out int seq)) return;
            if (!int.TryParse(parts[3], out int x)) return;
            if (!int.TryParse(parts[4], out int y)) return;

            // TCP로 로그인한 세션을 찾습니다
            ClientSession? session = GameServer.Instance.FindSession(playerId);
            if (session == null) return;

            // 첫 UDP 패킷이면 주소를 기억해둡니다.
            // 앞으로 이 주소로 STATE를 보내줄 겁니다.
            if (session.UdpEndPoint == null)
            {
                session.UdpEndPoint = from;
                Console.WriteLine($"[UDP] {session.Nickname} 주소 등록 : {from}");
            }
            else if (!session.UdpEndPoint.Equals(from))
            {
                // [수정] 등록된 주소가 아닌 곳에서 남의 PlayerId 로 온 MOVE 는 버린다
                Interlocked.Increment(ref _droppedCount);
                return;
            }

            // 오래된 패킷은 버립니다.
            // UDP는 순서를 보장하지 않아서 5번보다 6번이 먼저 올 수 있습니다.
            // 그때 5번을 적용하면 캐릭터가 뒤로 갑니다.
            if (seq <= session.LastSeq)   // [수정] < 이면 같은 번호(중복)가 다시 적용된다
            {
                Interlocked.Increment(ref _droppedCount);
                return;
            }

            session.LastSeq = seq;
            session.X = x;
            session.Y = y;

            Interlocked.Increment(ref _appliedCount);
        }

        // ═══════════════════════════════════════
        // 브로드캐스트 루프 — 초당 30번 전체 위치 전송
        // ═══════════════════════════════════════
        private async Task BroadcastLoopAsync()
        {
            while (true)
            {
                await Task.Delay(TICK_MS);

                var sessions = GameServer.Instance.GetSessions();
                if (sessions.Count == 0) continue;

                _stateBuilder.Clear();
                _stateBuilder.Append(UdpPacket.STATE);

                foreach (var s in sessions)
                {
                    if (s.UdpEndPoint == null) continue;
                    _stateBuilder.Append($"|{s.PlayerId},{s.Nickname},{s.X},{s.Y}");
                }

                string state = _stateBuilder.ToString();
                if (state == UdpPacket.STATE) continue;

                byte[] data = Encoding.UTF8.GetBytes(state);

                foreach (var s in sessions)
                {
                    if (s.UdpEndPoint == null) continue;

                    try
                    {
                        await _socket.SendToAsync(
                            new ArraySegment<byte>(data), SocketFlags.None, s.UdpEndPoint);
                    }
                    catch { }
                }
            }
        }
    }
}
