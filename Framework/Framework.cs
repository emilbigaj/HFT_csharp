using Tools;
using Data;
using Execution;

namespace Core
{
    public class Framework
    {
        public AlertManager AlertManager { get; }
        public Monitor Monitor { get; }
        public RiskManager RiskManager { get; }
        public ExecutionManager ExecutionManager { get; } = new ExecutionManager();

        public Framework()
        {
            AlertManager = new AlertManager();
            Monitor = new Monitor();
        }
    }
}
