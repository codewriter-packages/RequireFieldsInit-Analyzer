using System;

[AttributeUsage(AttributeTargets.Struct)]
public class RequireFieldsInitAttribute : Attribute
{
    public string[] Required { get; set; }
    public string[] Optional { get; set; }
}