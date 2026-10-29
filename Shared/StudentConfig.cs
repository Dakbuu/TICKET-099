using System;

namespace BattleArena
{
    /// <summary>
    /// ═══════════════════════════════════════════════
    ///  과제 설정 — 가장 먼저 여기를 수정하세요
    ///
    ///  이 파일은 세 프로젝트가 공유합니다.
    ///  (OutsourcedServer / TestClient / LoadTester)
    ///
    ///  한 번만 고치면 세 곳에 모두 반영됩니다.
    /// ═══════════════════════════════════════════════
    /// </summary>
    public static class StudentConfig
    {
        // ╔══════════════════════════════════════════╗
        // ║  ★ 본인 학번을 적으세요 (숫자만)          ║
        // ║                                          ║
        // ║    예) 23230881                          ║
        // ╚══════════════════════════════════════════╝
        public static readonly int STUDENT_ID = 22220754;

        // ╔══════════════════════════════════════════╗
        // ║  ★ 본인 이름을 적으세요                   ║
        // ╚══════════════════════════════════════════╝
        public static readonly string STUDENT_NAME = "전누리";

        // ───────────────────────────────────────────
        // 아래는 건드리지 마세요
        //
        // 포트를 학번에서 계산합니다.
        // 제출하는 모든 로그와 측정 파일에 이 포트가 찍히므로
        // 남의 결과를 그대로 낼 수 없습니다.
        //
        //   예)  23230881  →  TCP 11004 · UDP 11005
        //
        // ── 왜 1999 로 나누나 ──
        //   2000 은 1,000,000 의 약수입니다.
        //   그래서 학년도만 다르고 뒷자리가 같으면 값이 같아집니다.
        //
        //       23230881 % 2000 = 881
        //       24230881 % 2000 = 881     ← 같은 포트
        //
        //   1999 는 소수라서 이런 일이 생기지 않습니다.
        //
        //       23230881 % 1999 =  502
        //       24230881 % 1999 = 1002
        //
        // ── 왜 2 를 곱하나 ──
        //   TCP 는 짝수, UDP 는 홀수가 됩니다.
        //   내 UDP 포트가 남의 TCP 포트와 겹칠 일이 없습니다.
        //
        //   포트 범위 : TCP 10000~13996 · UDP 10001~13997
        // ───────────────────────────────────────────
        public static int TcpPort => 10000 + (STUDENT_ID % 1999) * 2;
        public static int UdpPort => TcpPort + 1;

        /// <summary>로그·파일명에 붙일 식별자</summary>
        public static string Stamp => $"{STUDENT_ID}_{STUDENT_NAME}";

        /// <summary>학번을 안 적었으면 실행을 막습니다</summary>
        public static void Verify()
        {
            if (STUDENT_ID <= 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine();
                Console.WriteLine("  ╔════════════════════════════════════════════╗");
                Console.WriteLine("  ║  학번을 먼저 입력하세요                      ║");
                Console.WriteLine("  ║                                            ║");
                Console.WriteLine("  ║  Shared/StudentConfig.cs 를 열어서          ║");
                Console.WriteLine("  ║  STUDENT_ID 와 STUDENT_NAME 을 채우세요      ║");
                Console.WriteLine("  ╚════════════════════════════════════════════╝");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("  아무 키나 누르면 종료합니다.");
                Console.ReadKey();
                Environment.Exit(1);
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine($"  학번 {STUDENT_ID} / {STUDENT_NAME}");
            Console.WriteLine($"  TCP {TcpPort}  ·  UDP {UdpPort}");
            Console.WriteLine();
        }
    }
}
