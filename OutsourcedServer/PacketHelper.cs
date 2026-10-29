using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace BattleArena
{
    /// <summary>
    /// TCP 패킷 송수신 담당
    ///
    /// 패킷 형식 : [ 본문길이(4바이트) ][ JSON 본문 ]
    ///
    /// 길이를 앞에 붙이는 이유는 TCP가 경계를 보장하지 않기 때문입니다.
    /// </summary>
    public static class PacketHelper
    {
        /// <summary>한 패킷의 본문 최대 크기</summary>
        private const int MAX_BODY = 8192;

        // ═══════════════════════════════════════
        // 전송
        // ═══════════════════════════════════════
        public static async Task SendAsync(Socket socket, object packet)
        {
            string json = JsonConvert.SerializeObject(packet);
            byte[] body = Encoding.UTF8.GetBytes(json);

            byte[] full = new byte[4 + body.Length];
            BitConverter.GetBytes(body.Length).CopyTo(full, 0);
            body.CopyTo(full, 4);

            int sent = 0;
            while (sent < full.Length)
            {
                sent += await socket.SendAsync(
                    new ArraySegment<byte>(full, sent, full.Length - sent),
                    SocketFlags.None);
            }
        }

        // ═══════════════════════════════════════
        // 수신
        // ═══════════════════════════════════════
        public static async Task<string?> ReceiveAsync(Socket socket)
        {
            byte[] header = new byte[4];

            // [수정] 블로킹 Receive 를 Task.Run 으로 감싸면 접속자 1명이 워커 스레드 1개를
            //        '대기하는 내내' 붙잡는다 → 접속자가 코어 수를 넘으면 스레드 고갈.
            //        또 Receive 는 요청한 만큼 다 온다는 보장이 없다(분할 도착).
            //        → 진짜 비동기 ReceiveAsync 로, 요청한 바이트를 다 받을 때까지 반복.
            if (!await ReadExactAsync(socket, header, 4)) return null;   // 상대가 연결을 닫음

            int bodyLength = BitConverter.ToInt32(header, 0);

            // 말도 안 되는 길이가 오면 끊습니다 (악성 패킷 방어)
            if (bodyLength <= 0 || bodyLength > MAX_BODY) return null;

            byte[] body = new byte[bodyLength];

            if (!await ReadExactAsync(socket, body, bodyLength)) return null;

            return Encoding.UTF8.GetString(body, 0, bodyLength);
        }

        /// <summary>size 바이트를 다 받을 때까지 반복. 중간에 끊기면 false</summary>
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
    }
}
