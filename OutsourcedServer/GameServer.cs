using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BattleArena
{
    /// <summary>
    /// ═══════════════════════════════════════════════
    ///  배틀아레나 게임 서버  (외주 납품본 v0.9)
    ///
    ///  TCP  로그인 · 채팅
    ///  UDP  위치 동기화
    /// ═══════════════════════════════════════════════
    /// </summary>
    public class GameServer
    {
        public static GameServer Instance { get; private set; } = null!;

        private Socket? _listenSocket;
        private int _nextPlayerId = 0;

        private readonly ConcurrentDictionary<int, ClientSession> _sessions = new();
        private readonly Stopwatch _uptime = new();

        private UdpServer? _udpServer;

        public GameServer()
        {
            Instance = this;
        }

        // ═══════════════════════════════════════
        // 서버 시작
        // ═══════════════════════════════════════
        public async Task StartAsync()
        {
            _uptime.Start();

            _listenSocket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);

            _listenSocket.Bind(new IPEndPoint(IPAddress.Any, StudentConfig.TcpPort));
            _listenSocket.Listen(512);

            Console.WriteLine($"[TCP] 포트 {StudentConfig.TcpPort}번에서 접속을 기다립니다");

            // UDP 서버를 같이 켭니다 (await를 붙이면 아래 Accept로 못 갑니다)
            _udpServer = new UdpServer();
            _ = _udpServer.StartAsync();

            // 2초마다 상태를 출력합니다 (측정 과제에서 씁니다)
            _ = StatsLoopAsync();

            while (true)
            {
                Socket clientSocket = await _listenSocket.AcceptAsync();

                clientSocket.NoDelay = true;

                int playerId = Interlocked.Increment(ref _nextPlayerId);

                ClientSession session = new ClientSession(clientSocket, playerId);
                _sessions.TryAdd(playerId, session);

                _ = session.RunAsync();
            }
        }

        // ═══════════════════════════════════════
        // 상태 출력 — 측정 과제용
        //
        // 이 줄을 캡처해서 제출물에 넣으세요.
        // 학번과 포트가 같이 찍힙니다.
        // ═══════════════════════════════════════
        private async Task StatsLoopAsync()
        {
            while (true)
            {
                await Task.Delay(2000);

                ThreadPool.GetAvailableThreads(out int worker, out int io);

                int applied = _udpServer?.AppliedCount ?? 0;
                int dropped = _udpServer?.DroppedCount ?? 0;

                Console.WriteLine(
                    $"[통계 {StudentConfig.STUDENT_ID}] " +
                    $"t={_uptime.Elapsed.TotalSeconds,5:F0}s | " +
                    $"접속 {_sessions.Count,4} | " +
                    $"스레드 {ThreadPool.ThreadCount,3} | " +
                    $"가용워커 {worker,5} | " +
                    $"UDP적용 {applied,6} | 버림 {dropped,5}");
            }
        }

        // ═══════════════════════════════════════
        // 세션 관리
        // ═══════════════════════════════════════
        public void RemoveSession(int playerId)
        {
            _sessions.TryRemove(playerId, out _);
        }

        public ClientSession? FindSession(int playerId)
        {
            _sessions.TryGetValue(playerId, out ClientSession? session);
            return session;
        }

        public List<ClientSession> GetSessions()
        {
            return new List<ClientSession>(_sessions.Values);
        }

        // ═══════════════════════════════════════
        // 브로드캐스트
        // ═══════════════════════════════════════
        public void Broadcast(object packet, int exceptPlayerId = -1)
        {
            foreach (var pair in _sessions)
            {
                if (pair.Key == exceptPlayerId) continue;
                if (!pair.Value.IsLoggedIn) continue;   // [수정] WELCOME 전인 세션에는 안 보냄
                _ = pair.Value.SendAsync(packet);
            }
        }
    }
}
