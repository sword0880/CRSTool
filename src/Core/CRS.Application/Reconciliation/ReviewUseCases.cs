using CRS.Application.Abstractions;

namespace CRS.Application;

/// <summary>人工复核用例统一校验依据，持久化端口只负责保存与查询。</summary>
public sealed class ReviewUseCases(ICalculationRepository repository)
{
    public Task<List<ReviewNote>> NotesAsync(string id) => Task.Run(() => repository.Reviews(id));
    public Task SaveAsync(string id, string category, string evidence) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 4000)
            throw new CrsException("请填写当前计算、复核类型和不超过 4000 字的依据。");
        repository.AddReview(id, category.Trim(), evidence.Trim());
    });
}
