using System.Threading.Tasks;
using Core.Models;

namespace Services.Interfaces
{
    public interface ITransactionService
    {
        Task SaveTransactionAsync(DeviceTransactionEventArgs transaction);
        Task ProcessMealHallTransactionAsync(DeviceTransactionEventArgs transaction);
    }
}

