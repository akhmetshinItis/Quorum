namespace Quorum.Api.Core;

public class StateMachine
{
    private readonly Dictionary<string, int> _state = new();
    
    public void Apply(string command) 
    {
        var commands = command.Split(" ");
        try 
        {
            switch(commands[0].ToUpper()) {
                // SET X Y
                case "SET":
                    _state[commands[1]] = int.Parse(commands[2]);
                    break;
                // CLEAR X
                case "CLEAR":
                    if (_state.ContainsKey(commands[1]))
                    {
                        _state.Remove(commands[1]);
                    }
                    break;
            }
        } 
        catch (System.FormatException) 
        { 
            // Don't apply bad requests
        }
    }
    
    public string RequestStatus(string param)
    {
        if (_state.TryGetValue(param, out var value))
        {
            return value.ToString();
        }

        return "";
    }

    public void TestConnection()
    {
        var testState = new Dictionary<string, int>();
        testState["X"] = int.Parse("0");
        testState.Clear();
    }

}