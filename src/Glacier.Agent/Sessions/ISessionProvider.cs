namespace Glacier.Agent.Sessions;

using System.Collections.Generic;
using System.Threading.Tasks;
using Glacier.Agent.Llm;

public interface ISessionProvider
{
    Task<List<LlmContent>> GetHistoryAsync(string sessionId);
    Task SaveMessageAsync(string sessionId, LlmContent message);
}
