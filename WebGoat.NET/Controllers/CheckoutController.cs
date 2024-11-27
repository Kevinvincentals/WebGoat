using WebGoatCore.Models;
using WebGoatCore.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using WebGoatCore.ViewModels;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using WebGoatCore.Exceptions;
using Stripe;
using Stripe.Checkout;
using System.Threading.Tasks;
using System.Collections.Generic;
using WebGoatCustomer = WebGoatCore.Models.Customer;

namespace WebGoatCore.Controllers
{
    public class CheckoutController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly CustomerRepository _customerRepository;
        private readonly ShipperRepository _shipperRepository;
        private readonly OrderRepository _orderRepository;
        private CheckoutViewModel? _model;
        private readonly IConfiguration _configuration;
        private readonly string _stripePublicKey;
        private readonly string _stripeSecretKey;

        public CheckoutController(
            UserManager<IdentityUser> userManager, 
            CustomerRepository customerRepository,
            IHostEnvironment hostEnvironment,
            IConfiguration configuration,
            ShipperRepository shipperRepository,
            OrderRepository orderRepository)
        {
            _userManager = userManager;
            _customerRepository = customerRepository;
            _shipperRepository = shipperRepository;
            _orderRepository = orderRepository;
            _configuration = configuration;
            
            // Read from environment variables
            _stripePublicKey = Environment.GetEnvironmentVariable("STRIPE_PUBLIC_KEY") 
                ?? throw new InvalidOperationException("Stripe public key not configured");
            _stripeSecretKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") 
                ?? throw new InvalidOperationException("Stripe secret key not configured");
            
            StripeConfiguration.ApiKey = _stripeSecretKey;
        }

        [HttpGet]
        public IActionResult Checkout()
        {
            var cart = HttpContext.Session.Get<Cart>("Cart");
            if (cart == null)
            {
                return RedirectToAction("Index", "Home");
            }

            if(_model == null)
            {
                InitializeModel();
            }

            _model.Cart = cart;

            ViewData["StripePublicKey"] = _stripePublicKey;
            return View(_model);
        }

        private void InitializeModel()
        {
            var customer = GetCustomerOrAddError();
            if (customer == null)
            {
                return;
            }

            _model = new CheckoutViewModel
            {
                ShipTarget = customer.ContactName ?? string.Empty,
                Address = customer.Address ?? string.Empty,
                City = customer.City ?? string.Empty,
                Region = customer.Region ?? string.Empty,
                PostalCode = customer.PostalCode ?? string.Empty,
                Country = customer.Country ?? string.Empty,
                Cart = HttpContext.Session.Get<Cart>("Cart")!
            };
        }

        [HttpPost]
        public async Task<IActionResult> Checkout([FromBody]CheckoutViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new { error = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage) });
            }

            try 
            {
                model.Cart = HttpContext.Session.Get<Cart>("Cart")!;
                
                if (model.Cart == null)
                {
                    return BadRequest(new { error = "Cart is empty" });
                }

                const decimal dkkRate = 7.5m;
                const decimal standardShipping = 20.0m;
                var subtotal = Convert.ToDecimal(model.Cart.SubTotal);
                
                var options = new SessionCreateOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = new List<SessionLineItemOptions>
                    {
                        new SessionLineItemOptions
                        {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                UnitAmount = (long)((subtotal * dkkRate) * 100m),
                                Currency = "dkk",
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = "Cart Items",
                                },
                            },
                            Quantity = 1,
                        },
                        new SessionLineItemOptions
                        {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                UnitAmount = (long)((standardShipping * dkkRate) * 100m),
                                Currency = "dkk",
                                ProductData = new SessionLineItemPriceDataProductDataOptions
                                {
                                    Name = "Standard Shipping",
                                },
                            },
                            Quantity = 1,
                        }
                    },
                    Mode = "payment",
                    SuccessUrl = $"{Request.Scheme}://{Request.Host}/Checkout/StripeSuccess?session_id={{CHECKOUT_SESSION_ID}}",
                    CancelUrl = $"{Request.Scheme}://{Request.Host}/Checkout/StripeCancel",
                    CustomerEmail = model.Email
                };

                var service = new SessionService();
                var session = await service.CreateAsync(options);

                return Json(new { sessionId = session.Id });
            }
            catch (Exception e)
            {
                return BadRequest(new { error = e.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> StripeSuccess(string session_id)
        {
            var sessionService = new SessionService();
            var session = await sessionService.GetAsync(session_id);

            // Create order from session data
            // ... implement order creation logic ...

            return RedirectToAction("Receipt");
        }

        [HttpGet]
        public IActionResult StripeCancel()
        {
            return RedirectToAction("Checkout");
        }

        public IActionResult Receipt(int? id)
        {
            var orderId = HttpContext.Session.GetInt32("OrderId");
            if (id != null)
            {
                orderId = id;
            }

            if (orderId == null)
            {
                ModelState.AddModelError(string.Empty, "No order specified. Please try again.");
                return View();
            }

            Order order;
            try
            {
                order = _orderRepository.GetOrderById(orderId.Value);
            }
            catch (InvalidOperationException)
            {
                ModelState.AddModelError(string.Empty, string.Format("Order {0} was not found.", orderId));
                return View();
            }

            return View(order);
        }

        public IActionResult Receipts()
        {
            var customer = GetCustomerOrAddError();
            if(customer == null)
            {
                return View();
            }

            return View(_orderRepository.GetAllOrdersByCustomerId(customer.CustomerId));
        }

        public IActionResult PackageTracking(string? carrier, string? trackingNumber)
        {
            var model = new PackageTrackingViewModel()
            {
                SelectedCarrier = carrier,
                SelectedTrackingNumber = trackingNumber,
            };

            var customer = GetCustomerOrAddError();
            if (customer != null)
            {
                model.Orders = _orderRepository.GetAllOrdersByCustomerId(customer.CustomerId);
            }
            
            return View(model);
        }

        public IActionResult GoToExternalTracker(string carrier, string trackingNumber)
        {
            return Redirect(Order.GetPackageTrackingUrl(carrier, trackingNumber));
        }

        private WebGoatCustomer? GetCustomerOrAddError()
        {
            var username = _userManager.GetUserName(User);
            var customer = _customerRepository.GetCustomerByUsername(username);
            if (customer == null)
            {
                ModelState.AddModelError(string.Empty, "I can't identify you. Please log in and try again.");
                return null;
            }

            return customer;
        }
    }
}