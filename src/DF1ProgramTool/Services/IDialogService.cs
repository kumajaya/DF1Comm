using System.Collections.Generic;
using System.Threading.Tasks;
using DF1ProgramTool.Models;

namespace DF1ProgramTool.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message);
    Task<string?> OpenFilePickerAsync(string title);
    Task<string?> SaveFilePickerAsync(string title, string suggestedFileName);
    Task ShowCompareResultsAsync<T>(List<T> results) where T : StructureCompareResult;
}
