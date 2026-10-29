using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace BattleArena
{
    /// <summary>
    /// ═══════════════════════════════════════════════
    ///  수동 테스트 클라이언트  (외주 납품본 v0.9)
    ///
    ///  서버와 같이 납품된 도구입니다.
    ///  이것도 인수 검토 대상입니다.
    ///
    ///  조작
    ///    W A S D   이동 (UDP로 전송)
    ///    C         채팅 보내기
    ///    R         연결 끊고 다시 접속
    ///    Q         종료
    /// ═══════════════════════════════════════════════
    /// </summary>
    public static class Program
    {
        private const string SERVER_IP = "127.0.0.1";

        private static Socket? _tcp;
        private static Socket? _udp;
        private static IPEndPoint? _udpServer;

        private static int _myId;
        private static int _seq;
        private static int _x = 5;
        private static int _y = 5;

        private static string _nick = "";
        private static string _lastState = "(아직 받은 것 없음)";
        private static string _udpError = "";
        private static string _tcpStatus = "미접속";

        /// <summary>채팅 입력 중에는 화면을 다시 그리지 않습니다</summary>
        private static bool _typing;

        /// <summary>움직일 수 있는 범위 (화면 지도 크기와 같습니다)</summary>
        private const int MAP_W = 30;
        private const int MAP_H = 8;

        private static readonly ConcurrentQueue<string> _chatLog = new();

        public static async Task Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "배틀아레나 테스트 클라이언트";

            Console.WriteLine("╔══════════════════════════════════════════╗");
            Console.WriteLine("║   배틀아레나 테스트 클라이언트 v0.9        ║");
            Console.WriteLine("╚══════════════════════════════════════════╝");
            Console.WriteLine();

            StudentConfig.Verify();

            Console.Write("  닉네임 : ");
            _nick = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(_nick))
                _nick = "Tester" + new Random().Next(100, 999);

            await ConnectAsync();

            Console.CursorVisible = false;
            Console.Clear();

            // ── 메인 루프 ──
            //   키를 "기다리지" 않고 "있으면 꺼내는" 방식입니다.
            //   ReadKey 로 막아두면 화면을 다시 그릴 수가 없습니다.
            while (true)
            {
                bool moved = false;

                while (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                    // ★ KeyChar 가 아니라 Key 로 판정합니다.
                    //   한글 입력 상태에서는 KeyChar 에 'W' 가 아니라
                    //   'ㅈ' 이 들어와서 WASD 가 안 먹습니다.
                    switch (key.Key)
                    {
                        case ConsoleKey.W:
                        case ConsoleKey.UpArrow: _y++; moved = true; break;

                        case ConsoleKey.S:
                        case ConsoleKey.DownArrow: _y--; moved = true; break;

                        case ConsoleKey.A:
                        case ConsoleKey.LeftArrow: _x--; moved = true; break;

                        case ConsoleKey.D:
                        case ConsoleKey.RightArrow: _x++; moved = true; break;

                        case ConsoleKey.C: await SendChatAsync(); break;
                        case ConsoleKey.R: await ReconnectAsync(); break;

                        case ConsoleKey.Q:
                            Console.CursorVisible = true;
                            Cleanup();
                            return;
                    }
                }

                if (moved)
                {
                    // 범위를 먼저 맞추고 보냅니다 (밖으로 나간 좌표를 보내지 않도록)
                    _x = Math.Clamp(_x, 0, MAP_W - 1);
                    _y = Math.Clamp(_y, 0, MAP_H - 1);

                    await SendPositionAsync();
                }

                Render();
                await Task.Delay(50);
            }
        }

        // ═══════════════════════════════════════
        // 접속
        // ═══════════════════════════════════════
        private static async Task ConnectAsync()
        {
            try
            {
                _tcp = new Socket(AddressFamily.InterNetwork,
                                  SocketType.Stream, ProtocolType.Tcp);

                await _tcp.ConnectAsync(IPAddress.Parse(SERVER_IP), StudentConfig.TcpPort);

                _tcpStatus = "접속됨";
                _ = TcpReceiveLoopAsync();

                await SendPacketAsync(new { Type = 1, Nickname = _nick });
            }
            catch (Exception ex)
            {
                _tcpStatus = "접속 실패 : " + ex.Message;
            }
        }

        private static void StartUdp()
        {
            try
            {
                _udp = new Socket(AddressFamily.InterNetwork,
                                  SocketType.Dgram, ProtocolType.Udp);

                // [수정] Bind 없이 ReceiveFromAsync 를 먼저 부르면
                //        InvalidOperationException("You must call the Bind method...") 으로
                //        수신 루프가 바로 죽는다 → STATE 를 영영 못 받음
                _udp.Bind(new IPEndPoint(IPAddress.Any, 0));

                _udpServer = new IPEndPoint(IPAddress.Parse(SERVER_IP), StudentConfig.UdpPort);

                _seq = 0;
                _udpError = "";

                _ = UdpReceiveLoopAsync();
            }
            catch (Exception ex)
            {
                _udpError = "UDP 시작 실패 : " + ex.Message;
            }
        }

        private static async Task ReconnectAsync()
        {
            _chatLog.Enqueue("── 재접속 ──");

            try { _tcp?.Close(); } catch { }
            try { _udp?.Close(); } catch { }

            _tcp = null;
            _udp = null;
            _myId = 0;
            _tcpStatus = "재접속 중...";

            await Task.Delay(300);
            await ConnectAsync();
        }

        private static void Cleanup()
        {
            try { _tcp?.Close(); } catch { }
            try { _udp?.Close(); } catch { }
            Console.Clear();
            Console.WriteLine("종료했습니다.");
        }

        // ═══════════════════════════════════════
        // TCP
        // ═══════════════════════════════════════
        private static async Task SendPacketAsync(object packet)
        {
            if (_tcp == null) return;

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(packet);
            byte[] body = Encoding.UTF8.GetBytes(json);

            byte[] full = new byte[4 + body.Length];
            BitConverter.GetBytes(body.Length).CopyTo(full, 0);
            body.CopyTo(full, 4);

            int sent = 0;
            while (sent < full.Length)
            {
                sent += await _tcp.SendAsync(
                    new ArraySegment<byte>(full, sent, full.Length - sent),
                    SocketFlags.None);
            }
        }

        private static async Task SendChatAsync()
        {
            // 입력 중에는 화면을 다시 그리지 않습니다.
            // 안 그러면 타이핑하는 글자가 계속 지워집니다.
            _typing = true;

            try
            {
                Console.CursorVisible = true;
                Console.Clear();
                Console.SetCursorPosition(0, 0);
                Console.WriteLine();
                Console.WriteLine("  보낼 메시지를 입력하고 엔터 (그냥 엔터면 취소)");
                Console.WriteLine();
                Console.Write("  > ");

                string msg = Console.ReadLine() ?? "";

                if (!string.IsNullOrWhiteSpace(msg))
                    await SendPacketAsync(new { Type = 2, Message = msg });
            }
            finally
            {
                Console.CursorVisible = false;
                Console.Clear();
                _typing = false;
            }
        }

        private static async Task TcpReceiveLoopAsync()
        {
            byte[] header = new byte[4];

            while (_tcp != null)
            {
                try
                {
                    // 헤더 4바이트를 끝까지 읽습니다
                    if (!await ReadExactAsync(_tcp, header, 4)) break;

                    int len = BitConverter.ToInt32(header, 0);
                    if (len <= 0 || len > 8192) break;

                    byte[] body = new byte[len];
                    if (!await ReadExactAsync(_tcp, body, len)) break;

                    string json = Encoding.UTF8.GetString(body, 0, len);
                    HandleTcpPacket(json);
                }
                catch
                {
                    break;
                }
            }

            _tcpStatus = "연결 끊김";
        }

        /// <summary>요청한 바이트 수를 다 받을 때까지 반복해서 읽습니다</summary>
        private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, int size)
        {
            int read = 0;
            while (read < size)
            {
                int n = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, read, size - read), SocketFlags.None);

                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        private static void HandleTcpPacket(string json)
        {
            dynamic? p = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
            if (p == null) return;

            int type = (int)p.Type;

            if (type == 11)                       // WELCOME
            {
                _myId = (int)p.PlayerId;
                _chatLog.Enqueue((string)p.Message);

                // 내 ID를 받은 뒤에 UDP를 시작합니다
                StartUdp();
            }
            else if (type == 12)                  // CHAT_ECHO
            {
                _chatLog.Enqueue($"{(string)p.Nickname} : {(string)p.Message}");
            }
            else if (type == 20 || type == 21)    // 입장 / 퇴장
            {
                _chatLog.Enqueue((string)p.Message);
            }

            while (_chatLog.Count > 6) _chatLog.TryDequeue(out _);
        }

        // ═══════════════════════════════════════
        // UDP
        // ═══════════════════════════════════════
        private static async Task SendPositionAsync()
        {
            if (_udp == null || _udpServer == null || _myId == 0) return;

            try
            {
                _seq++;
                string msg = $"MOVE|{_myId}|{_seq}|{_x}|{_y}";
                byte[] data = Encoding.UTF8.GetBytes(msg);

                await _udp.SendToAsync(new ArraySegment<byte>(data),
                                       SocketFlags.None, _udpServer);
            }
            catch (Exception ex)
            {
                _udpError = "전송 실패 : " + ex.Message;
            }
        }

        private static async Task UdpReceiveLoopAsync()
        {
            byte[] buffer = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            while (_udp != null)
            {
                try
                {
                    var r = await _udp.ReceiveFromAsync(
                        new ArraySegment<byte>(buffer), SocketFlags.None, from);

                    _lastState = Encoding.UTF8.GetString(buffer, 0, r.ReceivedBytes);
                }
                catch (Exception ex)
                {
                    _udpError = $"수신 중단 : {ex.GetType().Name} {ex.Message}";
                    break;
                }
            }
        }

        // ═══════════════════════════════════════
        // 화면
        //
        //   줄 수를 항상 똑같이 그립니다.
        //   줄마다 창 너비까지 공백을 채워서
        //   이전에 쓴 글자가 남지 않게 합니다.
        // ═══════════════════════════════════════
        private static void Render()
        {
            if (_typing) return;

            var lines = new List<string>();

            lines.Add($"  학번 {StudentConfig.STUDENT_ID}  ·  TCP {StudentConfig.TcpPort}  ·  UDP {StudentConfig.UdpPort}");
            lines.Add($"  나 : {_nick} (ID {_myId})    위치 ({_x},{_y})    보낸 시퀀스 {_seq}");
            lines.Add($"  TCP : {_tcpStatus}");
            lines.Add("");

            // ── 지도 ──
            //
            //   나는 @ , 다른 사람은 A B C ... 로 표시합니다.
            //   닉네임 첫 글자를 쓰면 한글이 두 칸을 차지해서
            //   칸이 밀립니다.
            var players = ParseState();

            // 플레이어마다 기호 하나씩 배정
            var mark = new Dictionary<int, char>();
            char next = 'A';
            foreach (var p in players)
            {
                if (p.id == _myId) { mark[p.id] = '@'; continue; }
                mark[p.id] = next;
                if (next < 'Z') next++;
            }

            lines.Add("  ┌" + new string('─', MAP_W) + "┐");

            for (int row = MAP_H - 1; row >= 0; row--)   // 위가 y 큰 쪽
            {
                var buf = new char[MAP_W];
                for (int i = 0; i < MAP_W; i++) buf[i] = '.';

                foreach (var p in players)
                {
                    if (p.y != row) continue;
                    if (p.x < 0 || p.x >= MAP_W) continue;

                    buf[p.x] = mark.TryGetValue(p.id, out char m) ? m : 'o';
                }

                // 서버에서 아직 아무것도 못 받았어도 내 위치는 보여줍니다
                if (players.Count == 0 && _y == row && _x >= 0 && _x < MAP_W)
                    buf[_x] = '@';

                lines.Add("  │" + new string(buf) + "│");
            }

            lines.Add("  └" + new string('─', MAP_W) + "┘");

            // ── 누가 보이나 ──
            if (players.Count == 0)
            {
                lines.Add("  보이는 사람 : 없음    (@ 가 나입니다)");
            }
            else
            {
                var sb = new StringBuilder("  ");
                foreach (var p in players)
                {
                    char m = mark.TryGetValue(p.id, out char c) ? c : 'o';
                    string name = (p.id == _myId) ? "나" : p.nick;
                    sb.Append($"{m} {name}({p.x},{p.y})   ");
                }
                lines.Add(sb.ToString());
            }

            lines.Add(string.IsNullOrEmpty(_udpError) ? "" : $"  [UDP 문제] {_udpError}");

            // ── 채팅 4줄 고정 ──
            lines.Add("  ── 채팅 ──");

            var chat = new List<string>(_chatLog);
            int start = Math.Max(0, chat.Count - 4);
            for (int i = 0; i < 4; i++)
            {
                int idx = start + i;
                lines.Add(idx < chat.Count ? "  " + chat[idx] : "");
            }

            lines.Add("  W A S D (또는 방향키) 이동    C 채팅    R 재접속    Q 종료");

            // ── 출력 ──
            try
            {
                int w = Math.Max(40, Console.WindowWidth - 1);

                Console.SetCursorPosition(0, 0);

                for (int i = 0; i < lines.Count; i++)
                {
                    // 마지막 줄에 WriteLine 을 쓰면 화면이 한 줄 밀려서
                    // 다음 프레임부터 레이아웃이 어긋납니다
                    if (i == lines.Count - 1) Console.Write(Fit(lines[i], w));
                    else Console.WriteLine(Fit(lines[i], w));
                }
            }
            catch
            {
                // 창이 너무 작으면 이번 프레임은 건너뜁니다
            }
        }

        /// <summary>STATE 문자열을 플레이어 목록으로</summary>
        private static List<(int id, string nick, int x, int y)> ParseState()
        {
            var list = new List<(int, string, int, int)>();

            string s = _lastState;
            if (string.IsNullOrEmpty(s) || !s.StartsWith("STATE")) return list;

            string[] parts = s.Split('|');
            for (int i = 1; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(',');
                if (f.Length < 4) continue;

                if (!int.TryParse(f[0], out int id)) continue;
                if (!int.TryParse(f[2], out int x)) continue;
                if (!int.TryParse(f[3], out int y)) continue;

                list.Add((id, f[1], x, y));
            }

            return list;
        }

        /// <summary>창 너비에 맞춰 자르거나 공백을 채웁니다</summary>
        private static string Fit(string s, int width)
        {
            s = (s ?? "").Replace("\r", "").Replace("\n", " ");

            // 한글은 두 칸을 차지하므로 길이를 따로 셉니다
            int visual = 0;
            var sb = new StringBuilder();

            foreach (char c in s)
            {
                int cw = (c >= 0x1100 && c <= 0xD7A3) || (c >= 0xFF01 && c <= 0xFF60) ? 2 : 1;
                if (visual + cw > width) break;

                sb.Append(c);
                visual += cw;
            }

            return sb.ToString() + new string(' ', Math.Max(0, width - visual));
        }

    }
}
