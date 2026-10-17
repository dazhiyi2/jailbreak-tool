using AgentToolkit.Contracts;
using AgentToolkit.Core;

namespace AgentToolkit.Ui.Services;

// SEAM: 后端接管时只改这一行。
public static class ServiceLocator
{
    public static IAgentEngine CreateEngine() => new MultiTargetEngine();
}
