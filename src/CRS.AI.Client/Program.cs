using System.Text;
using CRS.AI.Client.Hosting;

// 重定向的命令行输入和输出统一使用 UTF-8，保留中文问题及答案。
if (Console.IsInputRedirected) Console.InputEncoding = Encoding.UTF8;
if (Console.IsOutputRedirected) Console.OutputEncoding = Encoding.UTF8;

return await ClientCommand.RunAsync(args);
