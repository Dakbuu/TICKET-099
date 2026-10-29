using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BattleArena
{
    /// <summary>
    /// ═══════════════════════════════════════════════
    ///  부하·시나리오 테스트 도구
    ///
    ///  ★ 이 프로젝트는 검토 대상이 아닙니다.
    ///    조교가 만든 측정 도구이며 정상 동작합니다.
    ///    고치지 말고 그대로 쓰세요.
    ///
    ///  어느 시나리오가 어떤 문제를 드러내는지는
    ///  알려주지 않습니다. 직접 관찰하세요.
    /// ═══════════════════════════════════════════════
    /// </summary>
    public static class Program
    {
        private const string SERVER_IP = "127.0.0.1";

        public static async Task Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            Console.WriteLine("╔══════════════════════════════════════════╗");
            Console.WriteLine("║   부하·시나리오 테스트 도구                ║");
            Console.WriteLine("╚══════════════════════════════════════════╝");
            Console.WriteLine();

            StudentConfig.Verify();

            while (true)
            {
                Console.WriteLine("────────────────────────────────────────────");
                Console.WriteLine("  1. 동시 접속 부하 (측정 · CSV 저장)");
                Console.WriteLine("  2. 분할 전송");
                Console.WriteLine("  3. 연속 전송 (버스트)");
                Console.WriteLine("  4. 비정상 패킷");
                Console.WriteLine("  5. UDP 중복 / 지난 번호 패킷");
                Console.WriteLine("  6. 재접속 반복");
                Console.WriteLine("  7. 두 명 동시 접속 후 위치 교차 전송");
                Console.WriteLine("  8. 내 PC 정보 출력");
                Console.WriteLine("  0. 종료");
                Console.WriteLine("────────────────────────────────────────────");
                Console.Write("  선택 : ");

                string? sel = Console.ReadLine();
                Console.WriteLine();

                try
                {
                    switch (sel)
                    {
                        case "1": await LoadTestAsync(); break;
                        case "2": await SplitSendAsync(); break;
                        case "3": await BurstSendAsync(); break;
                        case "4": await MalformedAsync(); break;
                        case "5": await UdpDuplicateAsync(); break;
                        case "6": await ReconnectLoopAsync(); break;
                        case "7": await CrossSendAsync(); break;
                        case "8": PrintMachineInfo(); break;
                        case "0": return;
                        default: Console.WriteLine("  없는 번호입니다."); break;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  [연결 끊김 또는 서버 없음] {ex.GetType().Name} : {ex.Message}");
                    Console.ResetColor();
                    Console.WriteLine("  서버가 떠 있는지 확인하세요.");
                }

                Console.WriteLine();
            }
        }

        // ═══════════════════════════════════════
        // 1. 동시 접속 부하
        // ═══════════════════════════════════════
        private static async Task LoadTestAsync()
        {
            Console.Write("  동시 접속 수 (엔터 = 10,50,100,300,500 전부) : ");
            string? input = Console.ReadLine();

            int[] steps;
            if (string.IsNullOrWhiteSpace(input))
                steps = new[] { 10, 50, 100, 300, 500 };
            else if (int.TryParse(input.Trim(), out int one) && one > 0)
                steps = new[] { one };
            else
            {
                Console.WriteLine("  숫자를 입력하세요.");
                return;
            }

            PrintMachineInfo();

            string csv = Path.GetFullPath($"측정결과_{StudentConfig.STUDENT_ID}.csv");
            bool isNew = !File.Exists(csv);

            using (var w = new StreamWriter(csv, append: true, Encoding.UTF8))
            {
                if (isNew)
                {
                    w.WriteLine("학번,이름,측정시각,코어수,TCP포트,동시접속수,성공,실패," +
                                "평균ms,최소ms,최대ms,p95ms,스레드수,가용워커");
                }

                foreach (int n in steps)
                {
                    Console.WriteLine($"  ── 동시 접속 {n}명 ──");

                    StepResult result = await RunOneStepAsync(n);

                    ThreadPool.GetAvailableThreads(out int worker, out _);

                    Console.WriteLine($"     성공 {result.Ok} / 실패 {result.Fail}");
                    Console.WriteLine($"     평균 {result.Avg:F1}ms   최소 {result.Min:F1}ms   " +
                                      $"최대 {result.Max:F1}ms   p95 {result.P95:F1}ms");
                    Console.WriteLine();

                    w.WriteLine($"{StudentConfig.STUDENT_ID},{StudentConfig.STUDENT_NAME}," +
                                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{Environment.ProcessorCount}," +
                                $"{StudentConfig.TcpPort},{n},{result.Ok},{result.Fail}," +
                                $"{result.Avg:F1},{result.Min:F1},{result.Max:F1},{result.P95:F1}," +
                                $"{ThreadPool.ThreadCount},{worker}");

                    // 서버가 정리할 시간을 줍니다
                    await Task.Delay(2500);
                }
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  CSV 저장 : {csv}");
            Console.ResetColor();
            Console.WriteLine("  이 파일을 제출물에 그대로 첨부하세요.");
            Console.WriteLine("  ★ 측정 중 서버 콘솔의 [통계] 줄도 같이 캡처하세요.");
        }

        private class StepResult
        {
            public int Ok;
            public int Fail;
            public double Avg;
            public double Min;
            public double Max;
            public double P95;
        }

        private static async Task<StepResult> RunOneStepAsync(int count)
        {
            var times = new List<double>();
            var sockets = new List<Socket>();
            var gate = new object();

            int ok = 0;
            int fail = 0;

            var tasks = new List<Task>();

            for (int i = 0; i < count; i++)
            {
                int index = i;

                tasks.Add(Task.Run(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    Socket? s = null;

                    try
                    {
                        s = new Socket(AddressFamily.InterNetwork,
                                       SocketType.Stream, ProtocolType.Tcp);

                        await s.ConnectAsync(IPAddress.Parse(SERVER_IP), StudentConfig.TcpPort);
                        await SendPacketAsync(s, new { Type = 1, Nickname = $"Bot{index}" });

                        // WELCOME 이 올 때까지 (최대 10초)
                        string? json = await ReceiveOneAsync(s, 10000);
                        sw.Stop();

                        bool good = json != null && json.Contains("\"Type\":11");

                        lock (gate)
                        {
                            if (good) { ok++; times.Add(sw.Elapsed.TotalMilliseconds); }
                            else { fail++; }

                            sockets.Add(s);
                        }

                        s = null;   // 아래 catch에서 중복 Close 하지 않도록
                    }
                    catch
                    {
                        lock (gate) { fail++; }
                        try { s?.Close(); } catch { }
                    }
                }));
            }

            await Task.WhenAll(tasks);

            var r = new StepResult { Ok = ok, Fail = fail };

            if (times.Count > 0)
            {
                times.Sort();
                r.Avg = times.Average();
                r.Min = times[0];
                r.Max = times[times.Count - 1];
                r.P95 = times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))];
            }

            foreach (Socket s in sockets) Close(s);

            return r;
        }

        // ═══════════════════════════════════════
        // 2. 분할 전송
        // ═══════════════════════════════════════
        private static async Task SplitSendAsync()
        {
            Console.WriteLine("  패킷 하나를 두 조각으로 나눠 보냅니다.");
            Console.WriteLine("  (거리가 먼 서버와 통신할 때 흔히 일어나는 일입니다)");
            Console.WriteLine();

            var (s, _) = await ConnectAndLoginAsync("SplitBot");
            var reader = new Framed(s);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                new { Type = 2, Message = "AAAA-분할전송-테스트-BBBB" });
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = BitConverter.GetBytes(body.Length);

            int half = body.Length / 2;

            Console.WriteLine($"  본문 {body.Length}바이트를 {half} + {body.Length - half} 로 쪼갭니다.");

            // 1) 헤더 + 앞 조각
            byte[] first = new byte[4 + half];
            header.CopyTo(first, 0);
            Array.Copy(body, 0, first, 4, half);
            await s.SendAsync(new ArraySegment<byte>(first), SocketFlags.None);

            Console.WriteLine("  앞 조각 전송 → 50ms 대기");
            await Task.Delay(50);

            // 2) 뒤 조각
            await s.SendAsync(
                new ArraySegment<byte>(body, half, body.Length - half), SocketFlags.None);

            Console.WriteLine("  뒤 조각 전송");
            await Task.Delay(300);

            // 3) 이어서 정상 패킷
            Console.WriteLine("  정상 패킷 하나 더 전송");
            try
            {
                await SendPacketAsync(s, new { Type = 2, Message = "정상패킷-그다음" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  → 전송 실패 : {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("  ── 서버가 돌려준 것 ──");

            int got = 0;
            for (int i = 0; i < 5; i++)
            {
                string? r = reader.Read(1500);
                if (r == null) break;
                Console.WriteLine($"  {Cut(r, 110)}");
                got++;
            }
            if (got == 0) Console.WriteLine("  (아무것도 안 돌아왔습니다)");

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ★ 서버 창이 아직 떠 있는지 확인하세요.");
            Console.ResetColor();

            Close(s);
        }

        // ═══════════════════════════════════════
        // 3. 연속 전송 (버스트)
        // ═══════════════════════════════════════
        private static async Task BurstSendAsync()
        {
            const int COUNT = 100;

            Console.WriteLine($"  채팅 패킷 {COUNT}개를 한 번의 Send로 몰아 보냅니다.");
            Console.WriteLine();

            var (s, _) = await ConnectAndLoginAsync("BurstBot");
            var reader = new Framed(s);

            var all = new List<byte>();
            for (int i = 1; i <= COUNT; i++)
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    new { Type = 2, Message = $"burst-{i:D3}" });
                byte[] body = Encoding.UTF8.GetBytes(json);

                all.AddRange(BitConverter.GetBytes(body.Length));
                all.AddRange(body);
            }

            byte[] payload = all.ToArray();
            Console.WriteLine($"  총 {payload.Length}바이트 전송");

            int sent = 0;
            while (sent < payload.Length)
            {
                sent += await s.SendAsync(
                    new ArraySegment<byte>(payload, sent, payload.Length - sent),
                    SocketFlags.None);
            }

            Console.WriteLine("  전송 완료. 돌아오는 것을 셉니다...");

            int received = 0;
            while (true)
            {
                string? r = reader.Read(2000);
                if (r == null) break;
                if (r.Contains("burst-")) received++;
            }

            Console.WriteLine();
            Console.WriteLine($"  보낸 개수   : {COUNT}");
            Console.WriteLine($"  돌아온 개수 : {received}");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ★ 두 숫자가 다르면 왜 다른지 설명해야 합니다.");
            Console.ResetColor();

            Close(s);
        }

        // ═══════════════════════════════════════
        // 4. 비정상 패킷
        // ═══════════════════════════════════════
        private static async Task MalformedAsync()
        {
            Console.WriteLine("  형식이 깨진 패킷을 보냅니다.");
            Console.WriteLine("  (인터넷에 서버를 열면 실제로 이런 트래픽이 들어옵니다)");
            Console.WriteLine();

            var (s, _) = await ConnectAndLoginAsync("BadBot");
            var reader = new Framed(s);

            Console.WriteLine("  1) Type 필드가 없는 JSON 전송");
            try { await SendRawAsync(s, "{\"Nickname\":\"없음\"}"); }
            catch (Exception ex) { Console.WriteLine($"     전송 실패 : {ex.Message}"); }

            await Task.Delay(800);

            Console.WriteLine("  2) 그 다음 정상 패킷 전송 — 서버가 살아있는지 확인");
            try
            {
                await SendPacketAsync(s, new { Type = 2, Message = "아직 살아있나요?" });

                string? r = reader.Read(2000);
                Console.WriteLine(r == null
                    ? "     → 응답 없음"
                    : $"     → {Cut(r, 100)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     → 전송 실패 : {ex.GetType().Name} {ex.Message}");
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ★ 서버 창을 지금 바로 확인하세요. 아직 떠 있습니까?");
            Console.WriteLine("  ★ 떠 있지 않다면, 서버 콘솔에 마지막으로 뭐가 찍혔는지 캡처하세요.");
            Console.ResetColor();

            Close(s);
        }

        // ═══════════════════════════════════════
        // 5. UDP 중복 / 지난 번호 패킷
        // ═══════════════════════════════════════
        private static async Task UdpDuplicateAsync()
        {
            Console.WriteLine("  UDP로 위치를 보냅니다.");
            Console.WriteLine();

            var (tcp, myId) = await ConnectAndLoginAsync("UdpBot");

            if (myId == 0)
            {
                Console.WriteLine("  WELCOME 을 못 받아서 PlayerId를 모릅니다. 중단합니다.");
                Close(tcp);
                return;
            }

            Console.WriteLine($"  내 PlayerId = {myId}");

            var udp = new Socket(AddressFamily.InterNetwork,
                                 SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Any, 0));

            var server = new IPEndPoint(IPAddress.Parse(SERVER_IP), StudentConfig.UdpPort);

            async Task Move(int seq, int x, int y)
            {
                byte[] d = Encoding.UTF8.GetBytes($"MOVE|{myId}|{seq}|{x}|{y}");
                await udp.SendToAsync(new ArraySegment<byte>(d), SocketFlags.None, server);
                await Task.Delay(15);
            }

            Console.WriteLine("  1) 시퀀스 1~20 정상 전송        (20개)");
            for (int i = 1; i <= 20; i++) await Move(i, i, 5);
            await Task.Delay(300);

            Console.WriteLine("  2) 시퀀스 11~20 한 번 더 전송   (중복 10개)");
            for (int i = 11; i <= 20; i++) await Move(i, 99, 99);
            await Task.Delay(300);

            Console.WriteLine("  3) 시퀀스 5~9 전송              (지난 번호 5개)");
            for (int i = 5; i <= 9; i++) await Move(i, 0, 0);
            await Task.Delay(600);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ── 제대로 동작한다면 ──");
            Console.WriteLine("     적용 20개 · 버림 15개 (중복 10 + 지난 번호 5)");
            Console.WriteLine("     내 마지막 위치는 (20, 5) 여야 합니다.");
            Console.WriteLine();
            Console.WriteLine("  ★ 서버 콘솔 [통계] 줄의 UDP적용 / 버림 숫자를 확인하세요.");
            Console.WriteLine("  ★ 숫자가 다르면 어느 코드 때문인지 찾아야 합니다.");
            Console.ResetColor();

            try { udp.Close(); } catch { }
            Close(tcp);
        }

        // ═══════════════════════════════════════
        // 6. 재접속 반복
        // ═══════════════════════════════════════
        private static async Task ReconnectLoopAsync()
        {
            const int ROUNDS = 5;

            Console.WriteLine($"  접속 → 위치 전송 → 종료 를 {ROUNDS}번 반복합니다.");
            Console.WriteLine("  매 회차마다 서버가 내 위치를 돌려주는지 확인합니다.");
            Console.WriteLine();

            for (int round = 1; round <= ROUNDS; round++)
            {
                Socket? tcp = null;
                int myId = 0;

                try
                {
                    var r = await ConnectAndLoginAsync($"ReBot{round}");
                    tcp = r.sock;
                    myId = r.playerId;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {round}회차 : 접속 실패 — {ex.Message}");
                    continue;
                }

                if (tcp == null) continue;

                if (myId == 0)
                {
                    Console.WriteLine($"  {round}회차 : 로그인 실패 (WELCOME 못 받음)");
                    Close(tcp);
                    continue;
                }

                var udp = new Socket(AddressFamily.InterNetwork,
                                     SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Any, 0));

                var server = new IPEndPoint(IPAddress.Parse(SERVER_IP), StudentConfig.UdpPort);
                int myPort = ((IPEndPoint)udp.LocalEndPoint!).Port;

                for (int i = 1; i <= 10; i++)
                {
                    byte[] d = Encoding.UTF8.GetBytes($"MOVE|{myId}|{i}|{round * 3}|{i}");
                    await udp.SendToAsync(new ArraySegment<byte>(d), SocketFlags.None, server);
                    await Task.Delay(20);
                }

                await Task.Delay(400);

                string got = "(못 받음)";
                try
                {
                    udp.ReceiveTimeout = 1500;
                    byte[] buf = new byte[2048];
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = udp.ReceiveFrom(buf, ref from);
                    got = Encoding.UTF8.GetString(buf, 0, n);
                }
                catch (Exception ex)
                {
                    got = "(" + ex.GetType().Name + ")";
                }

                bool mine = got.Contains($"|{myId},");

                Console.ForegroundColor = mine ? ConsoleColor.Green : ConsoleColor.Red;
                Console.WriteLine($"  {round}회차 : PlayerId {myId,3} · 내 UDP포트 {myPort,5} · " +
                                  $"STATE에 내가 {(mine ? "있음" : "없음")}");
                Console.ResetColor();
                Console.WriteLine($"          {Cut(got, 90)}");

                try { udp.Close(); } catch { }
                Close(tcp);

                await Task.Delay(700);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ★ 회차에 따라 결과가 달라지면 그 이유를 설명해야 합니다.");
            Console.ResetColor();
        }

        // ═══════════════════════════════════════
        // 7. 두 명 동시 접속 후 위치 교차 전송
        // ═══════════════════════════════════════
        private static async Task CrossSendAsync()
        {
            Console.WriteLine("  봇 두 명을 접속시킨 뒤,");
            Console.WriteLine("  B가 A의 PlayerId를 적어서 위치 패킷을 보냅니다.");
            Console.WriteLine();

            var (tcpA, idA) = await ConnectAndLoginAsync("BotA");
            var (tcpB, idB) = await ConnectAndLoginAsync("BotB");

            if (idA == 0 || idB == 0)
            {
                Console.WriteLine("  로그인 실패. 중단합니다.");
                Close(tcpA); Close(tcpB);
                return;
            }

            Console.WriteLine($"  BotA = PlayerId {idA}");
            Console.WriteLine($"  BotB = PlayerId {idB}");
            Console.WriteLine();

            var udpA = new Socket(AddressFamily.InterNetwork,
                                  SocketType.Dgram, ProtocolType.Udp);
            udpA.Bind(new IPEndPoint(IPAddress.Any, 0));

            var udpB = new Socket(AddressFamily.InterNetwork,
                                  SocketType.Dgram, ProtocolType.Udp);
            udpB.Bind(new IPEndPoint(IPAddress.Any, 0));

            var server = new IPEndPoint(IPAddress.Parse(SERVER_IP), StudentConfig.UdpPort);

            async Task Send(Socket sock, int id, int seq, int x, int y)
            {
                byte[] d = Encoding.UTF8.GetBytes($"MOVE|{id}|{seq}|{x}|{y}");
                await sock.SendToAsync(new ArraySegment<byte>(d), SocketFlags.None, server);
                await Task.Delay(25);
            }

            Console.WriteLine($"  1) A가 자기 소켓으로  MOVE|{idA}|1|5|5     전송");
            await Send(udpA, idA, 1, 5, 5);

            Console.WriteLine($"  2) B가 자기 소켓으로  MOVE|{idB}|1|8|8     전송");
            await Send(udpB, idB, 1, 8, 8);

            await Task.Delay(400);

            Console.WriteLine($"  3) B가 자기 소켓으로  MOVE|{idA}|9999|30|15 전송  ← A의 ID");
            await Send(udpB, idA, 9999, 30, 15);

            await Task.Delay(500);

            // A의 소켓으로 STATE를 받아 A의 좌표를 확인
            string state = "(못 받음)";
            try
            {
                udpA.ReceiveTimeout = 1500;
                byte[] buf = new byte[2048];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n = udpA.ReceiveFrom(buf, ref from);
                state = Encoding.UTF8.GetString(buf, 0, n);
            }
            catch (Exception ex)
            {
                state = "(" + ex.GetType().Name + ")";
            }

            Console.WriteLine();
            Console.WriteLine("  ── A의 소켓으로 받은 STATE ──");
            Console.WriteLine($"  {Cut(state, 110)}");
            Console.WriteLine();

            bool moved = state.Contains($"|{idA},BotA,30,15");

            if (state.StartsWith("STATE") == false)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  STATE 를 못 받았습니다.");
                Console.WriteLine("  서버 콘솔 [통계] 줄의 UDP적용 숫자를 먼저 확인하세요.");
                Console.WriteLine("  0 에서 안 올라간다면, 해결해야 할 다른 문제가 있습니다.");
                Console.ResetColor();
            }
            else if (moved)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  A의 위치가 (30,15) 로 바뀌었습니다.");
                Console.WriteLine("  A는 그 패킷을 보낸 적이 없습니다.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  A의 위치가 (5,5) 에서 안 바뀌었습니다.");
                Console.ResetColor();
            }

            Console.WriteLine();
            Console.WriteLine("  ★ 이 결과가 정상인지 비정상인지 판단하고, 근거를 쓰세요.");

            try { udpA.Close(); } catch { }
            try { udpB.Close(); } catch { }
            Close(tcpA);
            Close(tcpB);
        }

        // ═══════════════════════════════════════
        // 8. 내 PC 정보
        // ═══════════════════════════════════════
        private static void PrintMachineInfo()
        {
            ThreadPool.GetAvailableThreads(out int worker, out int io);
            ThreadPool.GetMinThreads(out int minW, out int minIo);
            ThreadPool.GetMaxThreads(out int maxW, out int maxIo);

            Console.WriteLine("  ── 내 PC 정보 (제출물에 포함하세요) ──");
            Console.WriteLine($"     학번          : {StudentConfig.STUDENT_ID} {StudentConfig.STUDENT_NAME}");
            Console.WriteLine($"     측정 시각     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"     OS            : {Environment.OSVersion}");
            Console.WriteLine($"     논리 코어 수  : {Environment.ProcessorCount}");
            Console.WriteLine($"     .NET          : {Environment.Version}");
            Console.WriteLine($"     스레드풀 최소 : 워커 {minW} / IO {minIo}");
            Console.WriteLine($"     스레드풀 최대 : 워커 {maxW} / IO {maxIo}");
            Console.WriteLine($"     현재 가용     : 워커 {worker} / IO {io}");
            Console.WriteLine();
        }

        // ═══════════════════════════════════════
        // 공통 — 접속 + 로그인
        // ═══════════════════════════════════════
        private static async Task<(Socket sock, int playerId)> ConnectAndLoginAsync(string nick)
        {
            var s = new Socket(AddressFamily.InterNetwork,
                               SocketType.Stream, ProtocolType.Tcp);

            await s.ConnectAsync(IPAddress.Parse(SERVER_IP), StudentConfig.TcpPort);
            await SendPacketAsync(s, new { Type = 1, Nickname = nick });

            int playerId = 0;

            string? welcome = await ReceiveOneAsync(s, 5000);
            if (welcome != null && welcome.Contains("\"Type\":11"))
            {
                try
                {
                    dynamic? p = Newtonsoft.Json.JsonConvert.DeserializeObject(welcome);
                    if (p != null) playerId = (int)p.PlayerId;
                }
                catch { }
            }

            return (s, playerId);
        }

        // ═══════════════════════════════════════
        // 공통 — 송신
        // ═══════════════════════════════════════
        private static Task SendPacketAsync(Socket s, object packet)
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(packet);
            return SendRawAsync(s, json);
        }

        private static async Task SendRawAsync(Socket s, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);

            byte[] full = new byte[4 + body.Length];
            BitConverter.GetBytes(body.Length).CopyTo(full, 0);
            body.CopyTo(full, 4);

            int sent = 0;
            while (sent < full.Length)
            {
                sent += await s.SendAsync(
                    new ArraySegment<byte>(full, sent, full.Length - sent),
                    SocketFlags.None);
            }
        }

        // ═══════════════════════════════════════
        // 공통 — 수신
        // ═══════════════════════════════════════

        /// <summary>
        /// 패킷 하나만 비동기로 받습니다. (부하 측정용 — 스레드를 잡지 않습니다)
        /// 타임아웃이면 null.
        /// </summary>
        private static async Task<string?> ReceiveOneAsync(Socket s, int timeoutMs)
        {
            byte[] header = new byte[4];
            if (!await ReadExactAsync(s, header, 4, timeoutMs)) return null;

            int len = BitConverter.ToInt32(header, 0);
            if (len <= 0 || len > 65536) return null;

            byte[] body = new byte[len];
            if (!await ReadExactAsync(s, body, len, timeoutMs)) return null;

            return Encoding.UTF8.GetString(body, 0, len);
        }

        private static async Task<bool> ReadExactAsync(Socket s, byte[] buf, int size, int timeoutMs)
        {
            int read = 0;

            while (read < size)
            {
                Task<int> recv = s.ReceiveAsync(
                    new ArraySegment<byte>(buf, read, size - read), SocketFlags.None);

                Task done = await Task.WhenAny(recv, Task.Delay(timeoutMs));
                if (done != (Task)recv) return false;      // 타임아웃

                int n = await recv;
                if (n == 0) return false;
                read += n;
            }

            return true;
        }

        /// <summary>
        /// 길이 프리픽스 패킷을 순서대로 꺼내 읽는 도우미.
        /// 남은 바이트를 내부에 보관하므로 여러 번 읽어도 어긋나지 않습니다.
        /// </summary>
        private class Framed
        {
            private readonly Socket _s;
            private readonly List<byte> _buf = new List<byte>();

            public Framed(Socket s) { _s = s; }

            /// <summary>패킷 하나를 받습니다. 타임아웃·종료면 null.</summary>
            public string? Read(int timeoutMs)
            {
                try { _s.ReceiveTimeout = timeoutMs; } catch { return null; }

                while (true)
                {
                    if (_buf.Count >= 4)
                    {
                        byte[] h = new byte[4];
                        for (int i = 0; i < 4; i++) h[i] = _buf[i];

                        int len = BitConverter.ToInt32(h, 0);

                        if (len <= 0 || len > 65536) return null;

                        if (_buf.Count >= 4 + len)
                        {
                            byte[] body = new byte[len];
                            for (int i = 0; i < len; i++) body[i] = _buf[4 + i];

                            _buf.RemoveRange(0, 4 + len);
                            return Encoding.UTF8.GetString(body, 0, len);
                        }
                    }

                    byte[] tmp = new byte[8192];
                    int n = 0;

                    try { n = _s.Receive(tmp); }
                    catch { return null; }          // 타임아웃 포함

                    if (n == 0) return null;

                    for (int i = 0; i < n; i++) _buf.Add(tmp[i]);
                }
            }
        }

        // ═══════════════════════════════════════
        // 잡동사니
        // ═══════════════════════════════════════
        private static void Close(Socket s)
        {
            try { s.Shutdown(SocketShutdown.Both); } catch { }
            try { s.Close(); } catch { }
        }

        private static string Cut(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
