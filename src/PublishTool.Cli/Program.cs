using PublishTool.Cli;
using PublishTool.Commands;

var output = new ConsoleOutputSink();
var rootCommand = CommandLineFactory.Create(output);
var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();
