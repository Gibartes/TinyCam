using System.Diagnostics;
using TinyCam.Models;

namespace TinyCam.Platform;

public interface IProcessGuardian
{
    ProcessStartInfo PrepareStartInfo(ProcessStartInfo psi, TinyCamConfig cfg);

    void Attach(Process proc, TinyCamConfig cfg);

    // “부드러운 종료” 시도(Unix: SIGTERM 그룹, Win: 대기만). timeout 내 종료됐으면 true
    Task<bool> TryGracefulTerminateAsync(Process proc, int timeoutMs);

    // 강제 종료(Unix: SIGKILL 그룹, Win: Kill(entireProcessTree:true)). 종료했는지 반환
    bool TryKill(Process proc, int waitMs);
}

