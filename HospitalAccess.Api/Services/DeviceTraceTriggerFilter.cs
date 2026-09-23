using HospitalAccess.Application.Diagnostics;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Marca a ORIGEM de todo comando disparado por uma requisição da tela, para o rastreamento de
/// desempenho conseguir separar "o operador pediu" de "a rotina de fundo estava passando".
///
/// Um filtro só, em vez do marcador espalhado por dezenas de ações: a origem é uma propriedade
/// da requisição inteira, não de cada método — e um marcador esquecido numa ação nova viraria
/// uma linha "desconhecido" no CSV justamente quando alguém fosse investigar.
/// </summary>
public sealed class DeviceTraceTriggerFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;
        var trigger = controllerName switch
        {
            "Beds" => DeviceTraceTrigger.BedManagement,
            _ => DeviceTraceTrigger.ManualCommand,
        };

        using var scope = DeviceTraceContext.Use(trigger);
        await next();
    }
}
