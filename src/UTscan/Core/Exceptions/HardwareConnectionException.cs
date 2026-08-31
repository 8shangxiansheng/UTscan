using System;

namespace UTscan.Core.Exceptions;

/// <summary>M-1：硬件连接事务异常——连接流程中任一阶段失败时抛出，供统一回滚捕获。</summary>
public class HardwareConnectionException : Exception
{
    public HardwareConnectionException(string message) : base(message) { }
    public HardwareConnectionException(string message, Exception inner) : base(message, inner) { }
}