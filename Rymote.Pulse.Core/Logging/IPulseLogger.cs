﻿using System;

namespace Rymote.Pulse.Core.Logging;

public interface IPulseLogger
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}