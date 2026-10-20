# 程序集上下文样本探针

[English](README.md)

运行 `dotnet build tools/ReloadLeakProbe/ReloadLeakProbe.csproj -c Release` 构建，再给生成的 DLL 传入 `pure 8` 或 `leaky 8`，选择合成样本和迭代次数。

每次迭代使用不可回收的程序集加载上下文。工具在垃圾回收后报告上下文数量和进程资源观测值。它不执行 PluginHost，不能证明可回收模块已释放，也不能确定生产环境内存成本。每次对比应使用新进程。
