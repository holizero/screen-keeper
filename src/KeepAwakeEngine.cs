using System;

namespace ScreenKeeper
{
    interface IKeepAwakePlatform
    {
        uint TickCount { get; }
        bool DesktopAvailable { get; }
        bool AnyKeyHeld { get; }
        bool GetIdle(out uint milliseconds);
        bool AcquirePower();
        void ReleasePower();
        bool Pulse(bool click);
    }

    sealed class WindowsPlatform : IKeepAwakePlatform
    {
        public uint TickCount { get { return unchecked((uint)Environment.TickCount); } }
        public bool DesktopAvailable { get { return Native.SessionActive() && Native.DesktopAvailable(); } }
        public bool AnyKeyHeld { get { return Native.ButtonsHeld(); } }
        public bool GetIdle(out uint milliseconds) { return Native.Idle(out milliseconds); }
        public bool AcquirePower() { return Native.SetThreadExecutionState(Native.Continuous | Native.Display | Native.SystemRequired) != 0; }
        public void ReleasePower() { Native.SetThreadExecutionState(Native.Continuous); }
        public bool Pulse(bool click) { return Native.Pulse(click); }
    }

    // 모든 메서드는 UI 스레드에서 호출합니다. 전원 요청도 같은 스레드에서 생성/해제합니다.
    sealed class KeepAwakeEngine : IDisposable
    {
        readonly IKeepAwakePlatform platform;
        bool powerHeld, sessionLocked, disconnected, suspended;
        uint lastPulse;
        public bool Running { get; private set; }
        public bool Paused { get; private set; }
        public long ActionCount { get; private set; }
        public bool Click { get; private set; }
        public uint Interval { get; private set; }
        public string Message { get; private set; }

        public KeepAwakeEngine(IKeepAwakePlatform value) { platform = value; Message = "시작을 누르면 화면 보호 방지를 켭니다."; }
        public bool Start(int seconds, bool click)
        {
            if (Running) return true;
            if (seconds < 5 || seconds > 3600) { Message = "동작 간격은 5~3600초로 설정해 주세요."; return false; }
            Interval = (uint)seconds * 1000; Click = click; ActionCount = 0;
            lastPulse = platform.TickCount; Running = true; Tick(); return Running;
        }
        public void Stop(string reason)
        { Running = false; Paused = false; Release(); Message = reason; }
        void Release() { if (powerHeld) { platform.ReleasePower(); powerHeld = false; } }
        void Pause()
        {
            Release();
            if (Running) { Paused = true; Message = "잠금·연결 해제·절전 상태에서 일시 대기 중입니다."; }
        }
        public void SetLocked(bool value) { sessionLocked = value; if (value) Pause(); }
        public void SetDisconnected(bool value) { disconnected = value; if (value) Pause(); }
        public void SetSuspended(bool value) { suspended = value; if (value) Pause(); }
        public void Tick()
        {
            if (!Running) return;
            if (sessionLocked || disconnected || suspended || !platform.DesktopAvailable) { Pause(); return; }
            if (!powerHeld)
            {
                if (!platform.AcquirePower()) { Stop("화면 유지 요청에 실패해 중지했습니다."); return; }
                powerHeld = true; lastPulse = platform.TickCount;
            }
            Paused = false;
            uint idle;
            if (!platform.GetIdle(out idle)) { Stop("입력 상태를 확인하지 못해 중지했습니다."); return; }
            uint sinceAction = Native.Elapsed(platform.TickCount, lastPulse);
            uint remaining = Math.Max(Interval > idle ? Interval - idle : 0, Interval > sinceAction ? Interval - sinceAction : 0);
            Message = "다음 동작까지 약 " + ((remaining + 999) / 1000) + "초 · 입력하면 다시 기다립니다.";
            if (!Native.IsDue(idle, sinceAction, Interval)) return;
            if (platform.AnyKeyHeld) { Message = "키 또는 마우스 버튼을 사용 중이어서 기다립니다."; return; }
            if (!platform.Pulse(Click)) { Stop("마우스 동작이 차단되어 중지했습니다."); return; }
            lastPulse = platform.TickCount; ActionCount++;
            Message = "다음 동작까지 약 " + (Interval / 1000) + "초 · 입력하면 다시 기다립니다.";
        }
        public void Dispose() { Stop("종료했습니다."); }
    }
}
