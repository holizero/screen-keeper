using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenKeeper.Tests
{
    sealed class FakePlatform : IKeepAwakePlatform
    {
        public uint Now = 0, IdleTime = 0;
        public bool Desktop = true, Held = false, IdleOk = true, PowerOk = true, InputOk = true, LastClick = false;
        public int Acquires = 0, Releases = 0, Pulses = 0;
        public uint TickCount { get { return Now; } }
        public bool DesktopAvailable { get { return Desktop; } }
        public bool AnyKeyHeld { get { return Held; } }
        public bool GetIdle(out uint time) { time = IdleTime; return IdleOk; }
        public bool AcquirePower() { Acquires++; return PowerOk; }
        public void ReleasePower() { Releases++; }
        public bool Pulse(bool click) { Pulses++; LastClick = click; return InputOk; }
        public void Advance(uint time) { Now = unchecked(Now + time); IdleTime += time; }
    }
    static class TestRunner
    {
        static int passed, failed;
        static void Check(string name, bool ok)
        { Console.WriteLine((ok ? "통과: " : "실패: ") + name); if (ok) passed++; else failed++; }
        static void EngineTests()
        {
            FakePlatform p = new FakePlatform();
            using (KeepAwakeEngine e = new KeepAwakeEngine(p))
            {
                Check("시작 시 전원 요청, 즉시 입력 없음", e.Start(30, false) && p.Acquires == 1 && p.Pulses == 0);
                p.Advance(29999); e.Tick(); Check("30초 미만 입력 없음", p.Pulses == 0);
                p.Advance(1); e.Tick(); Check("30초에 한 번 동작", p.Pulses == 1 && e.ActionCount == 1);
                e.Tick(); Check("같은 틱에서 중복 동작 방지", p.Pulses == 1);
                p.Advance(30000); p.IdleTime = 1000; e.Tick(); Check("최근 사용자 입력이 있으면 대기", p.Pulses == 1);
                p.Advance(29000); p.Held = true; e.Tick(); Check("버튼이나 키가 눌려 있으면 대기", p.Pulses == 1);
                p.Held = false; e.Tick(); Check("키를 놓으면 정상 동작", p.Pulses == 2);
                e.SetLocked(true); p.Advance(60000); e.Tick(); e.Tick();
                Check("잠금 중 입력 및 전원 재요청 방지", e.Paused && p.Pulses == 2 && p.Releases == 1 && p.Acquires == 1);
                e.SetLocked(false); e.Tick(); Check("잠금 해제 직후 입력 없음", !e.Paused && p.Acquires == 2 && p.Pulses == 2);
                p.Advance(30000); e.Tick(); Check("잠금 해제 후 간격 재계산", p.Pulses == 3);
                e.SetDisconnected(true); p.Advance(60000); e.Tick(); Check("원격 연결 해제 중 계속 대기", e.Paused && p.Acquires == 2 && p.Pulses == 3);
                e.SetDisconnected(false); e.Tick(); Check("재연결 후 즉시 클릭하지 않음", p.Acquires == 3 && p.Pulses == 3);
                e.SetSuspended(true); p.Advance(60000); e.Tick(); Check("절전 중 요청 해제 및 대기", e.Paused && p.Acquires == 3 && p.Pulses == 3);
                e.SetSuspended(false); e.Tick(); Check("절전 복귀 후 대기 시간 재설정", p.Acquires == 4 && p.Pulses == 3);
                p.Desktop = false; e.Tick(); p.Advance(60000); e.Tick(); Check("보안 데스크톱 입력 없음", e.Paused && p.Acquires == 4 && p.Pulses == 3);
                p.Desktop = true; e.Tick(); Check("데스크톱 복귀 직후 입력 없음", p.Acquires == 5 && p.Pulses == 3);
                e.Stop("중지"); e.Stop("중지"); Check("중지 시 전원 요청을 한 번 해제", !e.Running && !e.Paused && p.Releases == 5);
                p.Advance(60000); e.Tick(); Check("중지 후 입력 없음", p.Pulses == 3);
                e.Start(5, true); p.Advance(5000); e.Tick(); Check("명시적으로 선택한 클릭 방식 전달", p.LastClick && e.ActionCount == 1);
            }
            Check("종료 시 전원 요청 정리", p.Releases == p.Acquires);
            p = new FakePlatform { PowerOk = false };
            using (KeepAwakeEngine e = new KeepAwakeEngine(p)) Check("전원 요청 실패 시 중지", !e.Start(30, false) && !e.Running);
            p = new FakePlatform { IdleOk = false };
            using (KeepAwakeEngine e = new KeepAwakeEngine(p)) Check("입력 조회 실패 시 중지 및 전원 해제", !e.Start(30, false) && p.Releases == 1);
            p = new FakePlatform { InputOk = false };
            using (KeepAwakeEngine e = new KeepAwakeEngine(p))
            { e.Start(30, false); p.Advance(30000); e.Tick(); Check("입력 실패 시 중지 및 전원 해제", !e.Running && p.Releases == 1 && e.ActionCount == 0); }
            p = new FakePlatform { Now = uint.MaxValue - 14999 };
            using (KeepAwakeEngine e = new KeepAwakeEngine(p))
            { e.Start(30, false); p.Advance(30000); e.Tick(); Check("틱 카운터 순환 후 30초 동작", p.Pulses == 1); }
            p = new FakePlatform();
            using (KeepAwakeEngine e = new KeepAwakeEngine(p)) Check("잘못된 간격 거부", !e.Start(4, false) && !e.Start(3601, false) && p.Acquires == 0);
        }
        static void SettingsTests()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ScreenKeeper.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); string file = Path.Combine(directory, "한글 설정.txt");
            try
            {
                Check("설정이 없으면 기본값", AppSettings.Load(file).IntervalSeconds == 30);
                AppSettings settings = new AppSettings { IntervalSeconds = 17, Modifiers = 7, ShortcutKey = Keys.K };
                Check("한글 경로 설정 저장", settings.Save(file));
                AppSettings restored = AppSettings.Load(file);
                Check("간격과 사용자 단축키 복원", restored.IntervalSeconds == 17 && restored.Modifiers == 7 && restored.ShortcutKey == Keys.K);
                settings.IntervalSeconds = 31; Check("기존 설정 원자적 교체", settings.Save(file) && AppSettings.Load(file).IntervalSeconds == 31);
                using (FileStream held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check("설정 잠금 시 저장 실패 반환", !settings.Save(file));
                Check("저장 실패 후 이전 설정 보존", AppSettings.Load(file).IntervalSeconds == 31);
                Check("저장 실패 후 임시 파일 정리", Directory.GetFiles(directory).Length == 1);
                string previous = File.ReadAllText(file); settings.IntervalSeconds = -1;
                Check("잘못된 설정으로 기존 파일 덮어쓰지 않음", !settings.Save(file) && File.ReadAllText(file) == previous);
                File.WriteAllText(file, "45\n"); Check("1.0 설정 파일 호환", AppSettings.Load(file).IntervalSeconds == 45);
                File.WriteAllText(file, "90\n3\n119\n"); Check("1.1 단축키 설정 파일 호환", AppSettings.Load(file).ShortcutKey == Keys.F8);
                File.WriteAllText(file, "broken\n0\n9999\n"); restored = AppSettings.Load(file);
                Check("손상된 설정은 기본값 사용", restored.IntervalSeconds == 30 && restored.Modifiers == 3 && restored.ShortcutKey == Keys.F8);
                File.WriteAllText(file, new string('x', 17000)); Check("과도한 설정 파일 거부", AppSettings.Load(file).IntervalSeconds == 30);
            }
            finally { foreach (string item in Directory.GetFiles(directory)) File.Delete(item); Directory.Delete(directory); }
        }
        static void NativeTests()
        {
            uint idle; Check("실제 Windows 마지막 입력 조회", Native.Idle(out idle));
            using (Form firstWindow = new Form()) using (Form secondWindow = new Form())
            using (GlobalShortcut firstKey = new GlobalShortcut()) using (GlobalShortcut secondKey = new GlobalShortcut())
            {
                firstKey.Load(7, Keys.F23); secondKey.Load(7, Keys.F24);
                bool firstOk = firstKey.Bind(firstWindow.Handle), secondOk = secondKey.Bind(secondWindow.Handle);
                Check("실제 Windows 단축키 두 조합 등록", firstOk && secondOk);
                if (!firstOk || !secondOk)
                { Console.WriteLine("Windows 오류: " + firstKey.LastError + ", " + secondKey.LastError); return; }
                Check("같은 단축키 재적용", firstKey.TryApply(7, Keys.F23));
                Check("다른 창의 단축키와 충돌 감지", !firstKey.TryApply(7, Keys.F24));
                Check("충돌 후 기존 단축키 유지", firstKey.Registered && firstKey.Key == Keys.F23);
                secondKey.Dispose(); Check("사용자 지정 단축키로 변경", firstKey.TryApply(7, Keys.F24));
                secondKey.Load(7, Keys.F23); Check("이전 단축키 해제 확인", secondKey.Bind(secondWindow.Handle));
                firstKey.Dispose(); Check("종료 후 단축키 해제 확인", secondKey.TryApply(7, Keys.F24));
            }
        }
        [STAThread] static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                Check("입력 구조체 크기", Marshal.SizeOf(typeof(Native.Input)) == (IntPtr.Size == 8 ? 40 : 28));
                using (Stream license = typeof(Program).Assembly.GetManifestResourceStream("ScreenKeeper.LICENSE"))
                using (StreamReader reader = new StreamReader(license))
                    Check("EXE 안에 MIT 라이선스 포함", reader.ReadToEnd().Contains("Permission is hereby granted"));
                Check("음수 모니터 좌표 정규화", Native.Normalize(-1920, -1920, 3840) >= 0 && Native.Normalize(1919, -1920, 3840) <= 65535);
                using (GlobalShortcut key = new GlobalShortcut())
                {
                    Check("기본 단축키 Ctrl+Alt+F8", key.DisplayName == "Ctrl+Alt+F8");
                    Check("단독 문자와 Shift 전용 조합 거부", !GlobalShortcut.Valid(0, Keys.A) && !GlobalShortcut.Valid(4, Keys.F8));
                    Check("예약 키 및 알 수 없는 값 거부", !GlobalShortcut.Valid(3, Keys.F12) && !GlobalShortcut.Valid(8, Keys.F8) && !GlobalShortcut.Valid(3, (Keys)9999));
                    key.Load(7, Keys.K); Check("사용자 단축키 이름", key.DisplayName == "Ctrl+Alt+Shift+K");
                    key.Load(0, Keys.A); Check("잘못된 단축키 무시", key.Key == Keys.K);
                }
                EngineTests(); SettingsTests();
                if (Array.IndexOf(args, "--native") >= 0) NativeTests();
                else Console.WriteLine("Windows 전역 단축키 검사는 --native 옵션으로 별도 실행합니다.");
            }
            catch (Exception e) { Check("예외 없이 검사 완료", false); Console.WriteLine(e); }
            Console.WriteLine("결과: " + passed + "개 통과, " + failed + "개 실패. 실제 입력은 합성하지 않습니다.");
            return failed == 0 ? 0 : 1;
        }
    }
}
