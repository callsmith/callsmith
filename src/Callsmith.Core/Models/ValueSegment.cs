using System.Text.Json.Serialization;

namespace Callsmith.Core.Models;

/// <summary>
/// One segment of a composite environment variable value.
/// A variable value may be a mix of literal text (<see cref="StaticValueSegment"/>)
/// and dynamic lookup references (<see cref="DynamicValueSegment"/>).
/// For example: <c>"AccessToken "</c> (static) + <c>{requestName=…, path=…}</c> (dynamic).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StaticValueSegment), "static")]
[JsonDerivedType(typeof(DynamicValueSegment), "dynamic")]
[JsonDerivedType(typeof(MockDataSegment), "mock")]
public abstract class ValueSegment { }
