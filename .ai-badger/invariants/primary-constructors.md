# Primary constructors, always

Declare a C# class's or struct's constructor parameters as a primary constructor
(`public sealed class Store(ILogger<Store> logger, TimeProvider time)`) rather than a classic
constructor that only copies its arguments into fields. Use the parameters directly, or assign
them to a field or property initializer when they need a different name, a readonly guarantee or
validation. A classic constructor stays only where a primary constructor cannot express the type,
such as several constructors with different bodies. Project rule.
