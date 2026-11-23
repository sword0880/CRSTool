using System.Runtime.CompilerServices;

// 让集成验收复用内部传输与分发入口，注入临时管道和模拟 API 响应。
[assembly: InternalsVisibleTo("CRS.Tests.AiQuery")]
