using System;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class RequireFieldsInitDisableChecksInNamespaceAttribute : Attribute
{
    public string Namespace { get; set; }
}