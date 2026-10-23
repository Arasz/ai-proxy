using System.Reflection;
using Microsoft.Extensions.Logging;

namespace AiProxy.Tests.Unit.Logging;

/// <summary>An event id names one log line, so two messages in one Log class must never share it.</summary>
public class LogEventIdTests
{
    [Fact]
    public void Every_logger_message_in_a_log_class_has_its_own_event_id()
    {
        var messages = typeof(ProxyApplication).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(message => message.Attribute is not null)
            .ToList();

        var duplicates = messages
            .GroupBy(message => (Type: message.Method.DeclaringType!.FullName, message.Attribute!.EventId))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Type} uses {group.Key.EventId} for {string.Join(", ", group.Select(message => message.Method.Name))}")
            .ToList();

        Assert.NotEmpty(messages);
        Assert.Empty(duplicates);
    }
}
