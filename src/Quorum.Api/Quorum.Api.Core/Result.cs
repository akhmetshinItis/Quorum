namespace Quorum.Api.Core;

public class Result(Code code, int? leaderId = null)
{
    public Code Code { get; set; } = code;
    
    public int? LeaderId { get; set; } = leaderId;
}

public enum Code
{
    Success,
    RedirectToLeader,
    Error,
}