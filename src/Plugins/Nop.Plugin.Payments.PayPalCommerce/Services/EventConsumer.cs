﻿using Microsoft.AspNetCore.Http;
using Nop.Core.Domain.Gdpr;
using Nop.Core.Domain.Shipping;
using Nop.Core.Http.Extensions;
using Nop.Services.Common;
using Nop.Services.Events;
using Nop.Services.Localization;
using Nop.Services.Shipping;
using Nop.Web.Areas.Admin.Models.Common;
using Nop.Web.Areas.Admin.Models.Orders;
using Nop.Web.Areas.Admin.Models.Payments;
using Nop.Web.Framework.Events;
using Nop.Web.Framework.Models;
using Nop.Web.Models.Customer;

namespace Nop.Plugin.Payments.PayPalCommerce.Services;

/// <summary>
/// Represents the plugin event consumer
/// </summary>
public class EventConsumer :
    IConsumer<CustomerPermanentlyDeleted>,
    IConsumer<ModelPreparedEvent<BaseNopModel>>,
    IConsumer<ModelReceivedEvent<BaseNopModel>>,
    IConsumer<ShipmentCreatedEvent>,
    IConsumer<ShipmentTrackingNumberSetEvent>,
    IConsumer<SystemWarningCreatedEvent>
{
    #region Fields

    private readonly IGenericAttributeService _genericAttributeService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILocalizationService _localizationService;
    private readonly IShipmentService _shipmentService;
    private readonly PayPalCommerceServiceManager _serviceManager;
    private readonly PayPalCommerceSettings _settings;

    #endregion

    #region Ctor

    public EventConsumer(IGenericAttributeService genericAttributeService,
        IHttpContextAccessor httpContextAccessor,
        ILocalizationService localizationService,
        IShipmentService shipmentService,
        PayPalCommerceServiceManager serviceManager,
        PayPalCommerceSettings settings)
    {
        _genericAttributeService = genericAttributeService;
        _httpContextAccessor = httpContextAccessor;
        _localizationService = localizationService;
        _shipmentService = shipmentService;
        _serviceManager = serviceManager;
        _settings = settings;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Handle customer permanently deleted event
    /// </summary>
    /// <param name="eventMessage">Event message</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task HandleEventAsync(CustomerPermanentlyDeleted eventMessage)
    {
        //delete customer's payment tokens
        await _serviceManager.DeletePaymentTokensAsync(_settings, eventMessage.CustomerId);
    }

    /// <summary>
    /// Handle model prepared event
    /// </summary>
    /// <param name="eventMessage">Event message</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task HandleEventAsync(ModelPreparedEvent<BaseNopModel> eventMessage)
    {
        //exclude the plugin from payment providers list, we'll display it another way
        if (eventMessage.Model is PaymentMethodListModel paymentMethodsModel)
            paymentMethodsModel.Data = paymentMethodsModel.Data.Where(method => !string.Equals(method.SystemName, PayPalCommerceDefaults.SystemName));

        if (eventMessage.Model is not CustomerNavigationModel navigationModel)
            return;

        var (active, _) = await _serviceManager.IsActiveAsync(_settings);
        if (!active)
            return;

        var (tokens, _) = await _serviceManager.GetPaymentTokensAsync(_settings);
        if (!_settings.UseVault && !tokens.Any())
            return;

        //add a new menu item in the customer navigation
        var orderItem = navigationModel.CustomerNavigationItems.FirstOrDefault(item => item.Tab == (int)CustomerNavigationEnum.Orders);
        var position = navigationModel.CustomerNavigationItems.IndexOf(orderItem) + 1;
        navigationModel.CustomerNavigationItems.Insert(position, new()
        {
            RouteName = PayPalCommerceDefaults.Route.PaymentTokens,
            ItemClass = "paypal-payment-tokens",
            Tab = PayPalCommerceDefaults.PaymentTokensMenuTab,
            Title = await _localizationService.GetResourceAsync("Plugins.Payments.PayPalCommerce.PaymentTokens")
        });
    }

    /// <summary>
    /// Handle model received event
    /// </summary>
    /// <param name="eventMessage">Event message</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task HandleEventAsync(ModelReceivedEvent<BaseNopModel> eventMessage)
    {
        if (eventMessage.Model is not ShipmentModel shipmentModel)
            return;

        if (!PayPalCommerceServiceManager.IsConnected(_settings))
            return;

        if (!_settings.UseShipmentTracking)
            return;

        //save specified shipment carrier
        var (exists, carrier) = await _httpContextAccessor.HttpContext.Request.TryGetFormValueAsync(PayPalCommerceDefaults.ShipmentCarrierAttribute);
        if (exists)
        {
            var shipment = await _shipmentService.GetShipmentByIdAsync(shipmentModel.Id);
            if (shipment is not null)
                await _genericAttributeService.SaveAttributeAsync(shipment, PayPalCommerceDefaults.ShipmentCarrierAttribute, carrier);
            else if (!string.IsNullOrEmpty(carrier))
            {
                //when we add a new shipping, it's not in the db yet and we cannot save a generic attribute to it,
                //so we temporarily store the data in the context, we'll move it to the attribute later during the same request
                _httpContextAccessor.HttpContext.Items.TryAdd(PayPalCommerceDefaults.ShipmentCarrierAttribute, carrier);
            }
        }
    }

    /// <summary>
    /// Handle shipment created event
    /// </summary>
    /// <param name="eventMessage">Event message</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task HandleEventAsync(ShipmentCreatedEvent eventMessage)
    {
        if (!PayPalCommerceServiceManager.IsConnected(_settings))
            return;

        if (!_settings.UseShipmentTracking || eventMessage.Shipment is null)
            return;

        //move the saved data from context to the generic attribute
        if (_httpContextAccessor.HttpContext.Items.TryGetValue(PayPalCommerceDefaults.ShipmentCarrierAttribute, out var carrier))
        {
            await _genericAttributeService
                .SaveAttributeAsync(eventMessage.Shipment, PayPalCommerceDefaults.ShipmentCarrierAttribute, carrier.ToString());
        }
    }

    /// <summary>
    /// Handle shipment tracking number set event
    /// </summary>
    /// <param name="eventMessage">Event message</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task HandleEventAsync(ShipmentTrackingNumberSetEvent eventMessage)
    {
        if (!PayPalCommerceServiceManager.IsConnected(_settings))
            return;

        if (!_settings.UseShipmentTracking)
            return;

        await _serviceManager.SetTrackingAsync(_settings, eventMessage.Shipment);
    }

    /// <summary>
    /// Handle system warning created event
    /// </summary>
    /// <param name="eventMessage">Event message</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public Task HandleEventAsync(SystemWarningCreatedEvent eventMessage)
    {
        if (!PayPalCommerceServiceManager.IsConnected(_settings))
            return Task.CompletedTask;

        if (!_settings.MerchantIdRequired)
            return Task.CompletedTask;

        //the plugin was updated, but no merchant ID was specified
        var warning = _settings.SetCredentialsManually
            ? "PayPal Commerce plugin. Merchant ID is required for payments, please specify it on the plugin configuration page"
            : "PayPal Commerce plugin. PayPal account ID of the merchant was not set correctly when updating the plugin. " +
                "You should either complete onboarding process again on the plugin configuration page or " +
                "set the ID yourself on the All Settings page (you can find this ID in your PayPal account)";
        eventMessage.SystemWarnings.Add(new() { Level = SystemWarningLevel.Warning, DontEncode = false, Text = warning });

        return Task.CompletedTask;
    }

    #endregion
}