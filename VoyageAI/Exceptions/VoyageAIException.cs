namespace VoyageAI.Exceptions;

public class VoyageAIException : Exception
{
    public int StatusCode { get; }
    public string ErrorType { get; }

    public VoyageAIException(string message, int statusCode, string errorType) 
        : base(message)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
    }

    public VoyageAIException(string message, int statusCode, string errorType, Exception innerException) 
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
    }
}

public class VoyageAIRateLimitException : VoyageAIException
{
    public VoyageAIRateLimitException(string message) 
        : base(message, 429, "rate_limit_error")
    {
    }
}

public class VoyageAIAuthenticationException : VoyageAIException
{
    public VoyageAIAuthenticationException(string message) 
        : base(message, 401, "authentication_error")
    {
    }
}

public class VoyageAIBadRequestException : VoyageAIException
{
    public VoyageAIBadRequestException(string message) 
        : base(message, 400, "bad_request_error")
    {
    }
}

public class VoyageAIServerException : VoyageAIException
{
    public VoyageAIServerException(string message, int statusCode) 
        : base(message, statusCode, "server_error")
    {
    }
}