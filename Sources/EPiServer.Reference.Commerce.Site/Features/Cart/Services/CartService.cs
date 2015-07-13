﻿using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Core;
using EPiServer.Globalization;
using EPiServer.Reference.Commerce.Site.Features.Cart.Extensions;
using EPiServer.Reference.Commerce.Site.Features.Cart.Models;
using EPiServer.Reference.Commerce.Site.Features.Checkout.Models;
using EPiServer.Reference.Commerce.Site.Features.Product.Models;
using EPiServer.Reference.Commerce.Site.Features.Shared.Extensions;
using EPiServer.ServiceLocation;
using EPiServer.Web.Routing;
using Mediachase.Commerce;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Orders;
using Mediachase.Commerce.Orders.Managers;
using Mediachase.Commerce.Website.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EPiServer.Reference.Commerce.Site.Features.Cart.Services
{
    [ServiceConfiguration(typeof(ICartService), Lifecycle = ServiceInstanceScope.Unique)]
    public class CartService : ICartService
    {
        private readonly Func<string, CartHelper> _cartHelper;
        private readonly IContentLoader _contentLoader;
        private readonly ReferenceConverter _referenceConverter;
        private readonly CultureInfo _preferredCulture;
        private string _cartName = Mediachase.Commerce.Orders.Cart.DefaultName;
        private readonly UrlResolver _urlResolver;

        public CartService(Func<string, CartHelper> cartHelper, IContentLoader contentLoader, ReferenceConverter referenceConverter, UrlResolver urlResolver)
        {
            _cartHelper = cartHelper;
            _contentLoader = contentLoader;
            _referenceConverter = referenceConverter;
            _preferredCulture = ContentLanguage.PreferredCulture;
            _urlResolver = urlResolver;
        }

        public void InitializeAsWishList()
        {
            _cartName = Mediachase.Commerce.Website.Helpers.CartHelper.WishListName;
        }

        public decimal GetLineItemsTotalQuantity()
        {
            return CartHelper.Cart.GetAllLineItems().Sum(x => x.Quantity);
        }

        public IEnumerable<CartItem> GetCartItems()
        {
            if (CartHelper.IsEmpty)
            {
                return Enumerable.Empty<CartItem>();
            }

            var cartItems = new List<CartItem>();
            var lineItems = CartHelper.Cart.GetAllLineItems();

            // In order to show the images for the items in the cart, we need to load the variants
            var variants = _contentLoader.GetItems(lineItems.Select(x => _referenceConverter.GetContentLink(x.Code)),
                _preferredCulture).OfType<VariationContent>();

            foreach (var lineItem in lineItems)
            {
                VariationContent variant = variants.FirstOrDefault(x => x.Code == lineItem.Code);
                ProductContent product = _contentLoader.Get<ProductContent>(variant.GetParentProducts().FirstOrDefault());
                CartItem item = new CartItem
                {
                    Code = lineItem.Code,
                    DisplayName = lineItem.DisplayName,
                    ImageUrl = variant.GetAssets<IContentImage>(_contentLoader, _urlResolver).FirstOrDefault() ?? "",
                    ExtendedPrice = lineItem.ToMoney(lineItem.ExtendedPrice + lineItem.OrderLevelDiscountAmount),
                    PlacedPrice = lineItem.ToMoney(lineItem.PlacedPrice),
                    DiscountPrice = lineItem.ToMoney(Math.Round(((lineItem.PlacedPrice * lineItem.Quantity) - lineItem.Discounts.Cast<LineItemDiscount>().Sum(x => x.DiscountValue)) / lineItem.Quantity, 2)),
                    Quantity = lineItem.Quantity,
                    Url = lineItem.GetUrl(),
                    Variant = variant,
                    Discounts = lineItem.Discounts.Cast<LineItemDiscount>().Select(x => new OrderDiscountModel
                    {
                        Discount = new Money(x.DiscountAmount, new Currency(CartHelper.Cart.BillingCurrency)),
                        Displayname = x.DisplayMessage
                    })
                };

                if (product is FashionProduct)
                {
                    FashionProduct fashionProduct = (FashionProduct)product;
                    item.Brand = fashionProduct.Brand;
                    item.AvailableSizes = fashionProduct.AvailableSizes;
                }

                cartItems.Add(item);
            }

            return cartItems;
        }

        public Money GetTotal()
        {
            if (CartHelper.IsEmpty)
            {
                return ConvertToMoney(0);
            }

            return ConvertToMoney(CartHelper.Cart.Total);
        }

        public Money GetTotalDiscount()
        {
            decimal amount = 0;

            if (CartHelper.IsEmpty)
            {
                return ConvertToMoney(amount);
            }

            amount = CartHelper.Cart.GetAllLineItems().Sum(x => x.LineItemDiscountAmount);

            return ConvertToMoney(amount);
        }

        public bool AddToCart(string code, out string warningMessage)
        {
            var entry = CatalogContext.Current.GetCatalogEntry(code);
            CartHelper.AddEntry(entry);
            CartHelper.Cart.ProviderId = "frontend"; // if this is not set explicitly, place price does not get updated by workflow
            ValidateCart(out warningMessage);

            return CartHelper.LineItems.Select(x => x.Code).Contains(code);
        }

        public void ChangeQuantity(string code, decimal quantity)
        {
            if (quantity == 0)
            {
                RemoveLineItem(code);
            }
            var lineItem = CartHelper.Cart.GetLineItem(code);
            if (lineItem != null)
            {
                lineItem.Quantity = quantity;
                ValidateCart();
                AcceptChanges();
            }
        }

        public void RemoveLineItem(string code)
        {
            var lineItem = CartHelper.Cart.GetLineItem(code);
            if (lineItem != null)
            {
                PurchaseOrderManager.RemoveLineItemFromOrder(CartHelper.Cart, lineItem.LineItemId);
                ValidateCart();
                AcceptChanges();
            }
        }

        private void ValidateCart()
        {
            string warningMessage = null;
            ValidateCart(out warningMessage);
        }

        private void ValidateCart(out string warningMessage)
        {
            if (_cartName == Mediachase.Commerce.Website.Helpers.CartHelper.WishListName)
            {
                warningMessage = null;
                return;
            }

            var workflowResult = OrderGroupWorkflowManager.RunWorkflow(CartHelper.Cart, OrderGroupWorkflowManager.CartValidateWorkflowName);
            var warnings = OrderGroupWorkflowManager.GetWarningsFromWorkflowResult(workflowResult).ToArray();
            warningMessage = warnings.Any() ? String.Join(" ", warnings) : null;
        }

        public Money ConvertToMoney(decimal amount)
        {
            return new Money(amount, new Currency(CartHelper.Cart.BillingCurrency));
        }

        public Money GetSubTotal()
        {
            decimal amount = CartHelper.Cart.SubTotal + CartHelper.Cart.OrderForms.SelectMany(x => x.Discounts.Cast<OrderFormDiscount>()).Sum(x => x.DiscountAmount);

            return ConvertToMoney(amount);
        }

        public Money GetShippingSubTotal()
        {
            decimal amount = CartHelper.Cart.OrderForms.SelectMany(x => x.Shipments).Sum(x => x.ShipmentTotal) + CartHelper.Cart.OrderForms.SelectMany(x => x.Shipments).Sum(x => x.ShippingDiscountAmount);

            return ConvertToMoney(amount);
        }

        public Money GetShippingTotal()
        {
            return ConvertToMoney(CartHelper.Cart.ShippingTotal);
        }

        public Money GetTaxTotal()
        {
            return ConvertToMoney(CartHelper.Cart.TaxTotal);
        }

        public Money GetShippingTaxTotal()
        {
            decimal amount = CartHelper.Cart.ShippingTotal + CartHelper.Cart.TaxTotal;

            return ConvertToMoney(amount);
        }

        public Money GetOrderDiscountTotal()
        {
            decimal amount = GetOrderForms().SelectMany(x => x.Discounts.Cast<OrderFormDiscount>()).Sum(x => x.DiscountAmount);

            return ConvertToMoney(amount);
        }

        public Money GetShippingDiscountTotal()
        {
            decimal amount = GetOrderForms().SelectMany(x => x.Shipments).SelectMany(x => x.Discounts.Cast<ShipmentDiscount>()).Sum(x => x.DiscountAmount);

            return ConvertToMoney(amount);
        }

        public IEnumerable<OrderForm> GetOrderForms()
        {
            return CartHelper.Cart.OrderForms.Count == 0 ? new[] { new OrderForm() } : CartHelper.Cart.OrderForms.ToArray();
        }

        public void RunWorkflow(string workFlowName)
        {
            if (_cartName == Mediachase.Commerce.Website.Helpers.CartHelper.WishListName)
            {
                throw new ArgumentException("Running workflows are not supported for wishlist carts.");
            }

            CartHelper.RunWorkflow(workFlowName);
        }

        public void SaveCart()
        {
            AcceptChanges();
        }

        public void DeleteCart()
        {
            CartHelper.Cart.Delete();
            CartHelper.Cart.AcceptChanges();
        }

        private void AcceptChanges()
        {
            CartHelper.Cart.AcceptChanges();
        }

        private CartHelper CartHelper
        {
            get { return _cartHelper(_cartName); }
        }
    }
}