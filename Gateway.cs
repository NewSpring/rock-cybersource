﻿// <copyright>
// Copyright 2013 by the Spark Development Network
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceModel;
using System.Text.RegularExpressions;
using cc.newspring.CyberSource.ITransactionProcessor;
using Rock;
using Rock.Attribute;
using Rock.Financial;
using Rock.Model;
using Rock.VersionInfo;
using Rock.Web.Cache;

namespace cc.newspring.CyberSource
{
    /// <summary>
    /// CyberSource Payment Gateway
    /// </summary>
    [Description( "CyberSource Payment Gateway" )]
    [Export( typeof( GatewayComponent ) )]
    [ExportMetadata( "ComponentName", "CyberSource" )]
    [TextField( "Merchant ID", "The CyberSource merchant ID (case-sensitive)", true, "", "", 0, "MerchantID" )]
    [MemoField( "Transaction Key", "The CyberSource transaction key", true, "", "", 0, "TransactionKey", 1 )]
    [TextField( "Report User", "The CyberSource reporting user (case-sensitive)", true, "", "", 2, "ReportUser" )]
    [TextField( "Report Password", "The CyberSource reporting password (case-sensitive)", true, "", "", 3, "ReportPassword" )]
    [CustomRadioListField( "Mode", "Mode to use for transactions", "Live,Test", true, "Live", "", 4 )]
    [TextField( "Live Gateway URL", "The CyberSource endpoint url to use for live transactions", true, "https://ics2wsa.ic3.com/commerce/1.x/transactionProcessor/", "", 5 )]
    [TextField( "Test Gateway URL", "The CyberSource endpoint url to use for test transactions", true, "https://ics2wstesta.ic3.com/commerce/1.x/transactionProcessor/", "", 6 )]
    [BooleanField( "Prompt for Name On Card", "Should users be prompted to enter name on the card", false, "", 7, "PromptForName" )]
    [BooleanField( "Prompt for Bank Account Name", "Should users be prompted to enter a name for the bank account (in addition to routing and account numbers).", true, "", 8, "PromptForBankAccountName" )]
    [BooleanField( "Prompt for Billing Address", "Should users be prompted to enter billing address", true, "", 9, "PromptForAddress" )]
    public class Gateway : GatewayComponent
    {
        private static string GATEWAY_RESPONSE_SUCCESS = "100";

        // Endpoint URL attribute provided for host changes ONLY
        // Version updates require in-app service regeneration
        private static string GATEWAY_VERSION = "1.127";

        #region Gateway Component Implementation

        /// <summary>
        /// Gets a value indicating whether gateway provider needs first and last name on credit card as two distinct fields.
        /// </summary>
        /// <value>
        /// <c>true</c> if [split name on card]; otherwise, <c>false</c>.
        /// </value>
        public override bool SplitNameOnCard
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the gateway requires the name on card for CC processing
        /// </summary>
        /// <value>
        /// <c>true</c> if [name on card required]; otherwise, <c>false</c>.
        /// </value>
        public override bool PromptForNameOnCard( FinancialGateway financialGateway )
        {
            return GetAttributeValue( financialGateway, "PromptForName" ).AsBoolean();
        }

        /// <summary>
        /// Prompts the name of for bank account.
        /// </summary>
        /// <param name="financialGateway">The financial gateway.</param>
        /// <returns></returns>
        public override bool PromptForBankAccountName( FinancialGateway financialGateway )
        {
            return GetAttributeValue( financialGateway, "PromptForBankAccountName" ).AsBoolean();
        }

        /// <summary>
        /// Gets a value indicating whether [address required].
        /// </summary>
        /// <value>
        /// <c>true</c> if [address required]; otherwise, <c>false</c>.
        /// </value>
        public override bool PromptForBillingAddress( FinancialGateway financialGateway )
        {
            return GetAttributeValue( financialGateway, "PromptForAddress" ).AsBoolean();
        }

        /// <summary>
        /// Determines if this gateway supports saved accounts of the type indicated by currencyType
        /// </summary>
        /// <param name="currencyType">The currency type</param>
        /// <returns></returns>
        public override bool SupportsSavedAccount( DefinedValueCache currencyType )
        {
            var ach = Rock.SystemGuid.DefinedValue.CURRENCY_TYPE_ACH.AsGuid();
            var creditCard = Rock.SystemGuid.DefinedValue.CURRENCY_TYPE_CREDIT_CARD.AsGuid();
            var currencyTypeGuid = currencyType.Guid;

            return currencyTypeGuid.Equals( ach ) || currencyTypeGuid.Equals( creditCard );
        }

        /// <summary>
        /// Gets the supported payment schedules.
        /// </summary>
        /// <value>
        /// The supported payment schedules.
        /// </value>
        public override List<DefinedValueCache> SupportedPaymentSchedules
        {
            get
            {
                var values = new List<DefinedValueCache>();
                values.Add( DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_ONE_TIME ) );
                values.Add( DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_WEEKLY ) );
                values.Add( DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_BIWEEKLY ) );
                values.Add( DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_TWICEMONTHLY ) );
                values.Add( DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_MONTHLY ) );
                values.Add( DefinedValueCache.Read( Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_YEARLY ) );
                return values;
            }
        }

        /// <summary>
        /// Credits (Refunds) the specified transaction.
        /// </summary>
        /// <param name="origTransaction">The original transaction.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override FinancialTransaction Credit( FinancialTransaction origTransaction, decimal amount, string comment, out string errorMessage )
        {
            errorMessage = string.Empty;

            if ( origTransaction == null )
            {
                errorMessage = "Original transaction cannot be null";
                return null;
            }

            if ( string.IsNullOrWhiteSpace( origTransaction.TransactionCode ) )
            {
                errorMessage = "The transaction must have a TransactionCode to process a refund/credit";
                return null;
            }

            if ( origTransaction.FinancialGateway == null )
            {
                errorMessage = "The transaction must have a FinancialGateway to process a refund/credit";
                return null;
            }

            var financialGateway = origTransaction.FinancialGateway;
            var request = GetMerchantInfo( financialGateway );

            request.ccCreditService = new CCCreditService
            {
                run = "true",
                captureRequestID = origTransaction.TransactionCode
            };

            request.comments = comment;
            request.purchaseTotals = GetTotals();
            request.purchaseTotals.grandTotalAmount = amount.ToString();

            // Credit the transaction
            ReplyMessage reply = SubmitTransaction( financialGateway, request, out errorMessage );
            if ( reply != null && reply.reasonCode.Equals( GATEWAY_RESPONSE_SUCCESS ) )
            {
                var transactionGuid = new Guid( reply.merchantReferenceCode );
                var transaction = new FinancialTransaction
                {
                    Guid = transactionGuid,
                    TransactionCode = reply.requestID,
                    Summary = comment
                };
                return transaction;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = string.Format( "Unable to credit this transaction.{0}", ProcessError( reply ) );
            }

            return null;
        }

        /// <summary>
        /// Authorizes/tokenizes the specified payment info.
        /// </summary>
        /// <param name="paymentInfo">The payment info.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override FinancialTransaction Authorize( FinancialGateway financialGateway, PaymentInfo paymentInfo, out string errorMessage )
        {
            errorMessage = string.Empty;
            RequestMessage request = GetPaymentInfo( financialGateway, paymentInfo );

            if ( request == null )
            {
                errorMessage = "Payment type not implemented";
                return null;
            }

            if ( request.recurringSubscriptionInfo == null )
            {
                request.recurringSubscriptionInfo = new RecurringSubscriptionInfo();
            }

            request.recurringSubscriptionInfo.frequency = "ON-DEMAND";
            request.recurringSubscriptionInfo.amount = paymentInfo.Amount.ToString();
            request.paySubscriptionCreateService = new PaySubscriptionCreateService();
            request.paySubscriptionCreateService.run = "true";
            request.purchaseTotals = GetTotals();
            request.billTo = GetBillTo( paymentInfo );
            request.item = GetItems( paymentInfo );

            request.subscription = new Subscription();
            if ( !paymentInfo.CurrencyTypeValue.Guid.Equals( new Guid( Rock.SystemGuid.DefinedValue.CURRENCY_TYPE_CREDIT_CARD ) ) )
            {
                request.subscription.paymentMethod = "check";
            }

            if ( paymentInfo is ReferencePaymentInfo )
            {
                request.paySubscriptionCreateService.paymentRequestID = ( (ReferencePaymentInfo)paymentInfo ).TransactionCode;
            }

            // Authorize the transaction
            ReplyMessage reply = SubmitTransaction( financialGateway, request, out errorMessage );
            if ( reply != null && reply.reasonCode.Equals( GATEWAY_RESPONSE_SUCCESS ) )
            {
                var transactionGuid = new Guid( reply.merchantReferenceCode );
                var transaction = new FinancialTransaction { Guid = transactionGuid };
                transaction.TransactionCode = reply.requestID;
                return transaction;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = string.Format( "Unable to authorize this transaction.{0}", ProcessError( reply ) );
            }

            return null;
        }

        /// <summary>
        /// Charges the specified payment info.
        /// </summary>
        /// <param name="paymentInfo">The payment info.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override FinancialTransaction Charge( FinancialGateway financialGateway, PaymentInfo paymentInfo, out string errorMessage )
        {
            errorMessage = string.Empty;
            RequestMessage request = GetPaymentInfo( financialGateway, paymentInfo );
            if ( request == null )
            {
                errorMessage = "Payment type not implemented";
                return null;
            }

            request.purchaseTotals = GetTotals();
            request.billTo = GetBillTo( paymentInfo );
            request.item = GetItems( paymentInfo );

            if ( !paymentInfo.CurrencyTypeValue.Guid.Equals( new Guid( Rock.SystemGuid.DefinedValue.CURRENCY_TYPE_CREDIT_CARD ) ) )
            {
                request.ecDebitService = new ECDebitService();
                request.ecDebitService.commerceIndicator = "internet";
                request.ecDebitService.run = "true";
            }
            else
            {
                request.ccAuthService = new CCAuthService();
                request.ccAuthService.commerceIndicator = "internet";
                request.ccAuthService.run = "true";
                request.ccCaptureService = new CCCaptureService();
                request.ccCaptureService.run = "true";
            }

            // Charge the transaction
            ReplyMessage reply = SubmitTransaction( financialGateway, request, out errorMessage );
            if ( reply != null && reply.reasonCode.Equals( GATEWAY_RESPONSE_SUCCESS ) )
            {
                var transactionGuid = new Guid( reply.merchantReferenceCode );
                var transaction = new FinancialTransaction { Guid = transactionGuid };
                transaction.TransactionCode = reply.requestID;
                return transaction;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = string.Format( "Unable to process this order.{0}", ProcessError( reply ) );
            }

            return null;
        }

        /// <summary>
        /// Adds the scheduled payment.
        /// </summary>
        /// <param name="schedule">The schedule.</param>
        /// <param name="paymentInfo">The payment info.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override FinancialScheduledTransaction AddScheduledPayment( FinancialGateway financialGateway, PaymentSchedule schedule, PaymentInfo paymentInfo, out string errorMessage )
        {
            errorMessage = string.Empty;
            RequestMessage request = GetPaymentInfo( financialGateway, paymentInfo );

            if ( request == null )
            {
                errorMessage = "Payment type not implemented";
                return null;
            }

            if ( request.recurringSubscriptionInfo == null )
            {
                request.recurringSubscriptionInfo = new RecurringSubscriptionInfo();
            }

            request.recurringSubscriptionInfo.startDate = GetStartDate( schedule );
            request.recurringSubscriptionInfo.frequency = GetFrequency( schedule );
            request.recurringSubscriptionInfo.amount = paymentInfo.Amount.ToString();
            request.paySubscriptionCreateService = new PaySubscriptionCreateService();
            request.paySubscriptionCreateService.run = "true";
            request.purchaseTotals = GetTotals();
            request.billTo = GetBillTo( paymentInfo );
            request.item = GetItems( paymentInfo );

            request.subscription = new Subscription();
            if ( !paymentInfo.CurrencyTypeValue.Guid.Equals( new Guid( Rock.SystemGuid.DefinedValue.CURRENCY_TYPE_CREDIT_CARD ) ) )
            {
                request.subscription.paymentMethod = "check";
            }

            if ( paymentInfo is ReferencePaymentInfo )
            {
                request.paySubscriptionCreateService.paymentRequestID = ( (ReferencePaymentInfo)paymentInfo ).TransactionCode;
            }

            // Schedule the payment
            ReplyMessage reply = SubmitTransaction( financialGateway, request, out errorMessage );
            if ( reply != null && reply.reasonCode.Equals( GATEWAY_RESPONSE_SUCCESS ) )
            {
                var transactionGuid = new Guid( reply.merchantReferenceCode );
                var scheduledTransaction = new FinancialScheduledTransaction { Guid = transactionGuid };
                scheduledTransaction.TransactionCode = reply.paySubscriptionCreateReply.subscriptionID;
                scheduledTransaction.GatewayScheduleId = reply.paySubscriptionCreateReply.subscriptionID;
                scheduledTransaction.FinancialGateway = financialGateway;
                scheduledTransaction.FinancialGatewayId = financialGateway.Id;
                GetScheduledPaymentStatus( scheduledTransaction, out errorMessage );
                return scheduledTransaction;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = string.Format( "Your order was not approved.{0}", ProcessError( reply ) );
            }

            return null;
        }

        /// <summary>
        /// Reactivates the scheduled payment (CyberSource not supported).
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override bool ReactivateScheduledPayment( FinancialScheduledTransaction transaction, out string errorMessage )
        {
            errorMessage = string.Empty;
            return false;
        }

        /// <summary>
        /// Updates the scheduled payment.
        /// </summary>
        /// <param name="schedule">The schedule.</param>
        /// <param name="paymentInfo">The payment info.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override bool UpdateScheduledPayment( FinancialScheduledTransaction transaction, PaymentInfo paymentInfo, out string errorMessage )
        {
            errorMessage = string.Empty;
            var financialGateway = transaction.FinancialGateway;
            RequestMessage request = GetPaymentInfo( financialGateway, paymentInfo );
            if ( request == null )
            {
                errorMessage = "Payment type not implemented";
                return false;
            }

            if ( request.recurringSubscriptionInfo == null )
            {
                request.recurringSubscriptionInfo = new RecurringSubscriptionInfo();
                request.recurringSubscriptionInfo.subscriptionID = transaction.TransactionCode;
            }
            request.recurringSubscriptionInfo.amount = paymentInfo.Amount.ToString();
            request.paySubscriptionUpdateService = new PaySubscriptionUpdateService();
            request.paySubscriptionUpdateService.run = "true";
            request.purchaseTotals = GetTotals();
            request.billTo = GetBillTo( paymentInfo );
            request.item = GetItems( paymentInfo );

            request.subscription = new Subscription();
            if ( !paymentInfo.CurrencyTypeValue.Guid.Equals( new Guid( Rock.SystemGuid.DefinedValue.CURRENCY_TYPE_CREDIT_CARD ) ) )
            {
                request.subscription.paymentMethod = "check";
            }
            else
            {
                request.subscription.paymentMethod = "credit card";
            }

            // Update the schedule
            ReplyMessage reply = SubmitTransaction( financialGateway, request, out errorMessage );
            if ( reply != null && reply.reasonCode.Equals( GATEWAY_RESPONSE_SUCCESS ) )
            {
                return true;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = string.Format( "Unable to update this transaction. {0}", ProcessError( reply ) );
            }

            return false;
        }

        /// <summary>
        /// Cancels the scheduled payment.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override bool CancelScheduledPayment( FinancialScheduledTransaction transaction, out string errorMessage )
        {
            errorMessage = string.Empty;
            var financialGateway = transaction.FinancialGateway;
            RequestMessage request = GetMerchantInfo( transaction.FinancialGateway, false );
            request.recurringSubscriptionInfo = new RecurringSubscriptionInfo();
            request.recurringSubscriptionInfo.subscriptionID = transaction.TransactionCode;
            request.paySubscriptionUpdateService = new PaySubscriptionUpdateService();
            request.recurringSubscriptionInfo.status = "cancel";
            request.paySubscriptionUpdateService.run = "true";

            // Cancel the payment
            ReplyMessage reply = SubmitTransaction( financialGateway, request, out errorMessage );
            if ( reply != null && reply.reasonCode.Equals( GATEWAY_RESPONSE_SUCCESS ) )
            {
                return true;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = string.Format( "Unable to cancel this transaction. {0}", ProcessError( reply ) );
            }

            return false;
        }

        /// <summary>
        /// Gets the scheduled payment status.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override bool GetScheduledPaymentStatus( FinancialScheduledTransaction transaction, out string errorMessage )
        {
            errorMessage = string.Empty;
            var financialGateway = transaction.FinancialGateway;
            RequestMessage verifyRequest = GetMerchantInfo( financialGateway );
            verifyRequest.paySubscriptionRetrieveService = new PaySubscriptionRetrieveService();
            verifyRequest.paySubscriptionRetrieveService.run = "true";
            verifyRequest.recurringSubscriptionInfo = new RecurringSubscriptionInfo();
            verifyRequest.recurringSubscriptionInfo.subscriptionID = transaction.TransactionCode;

            // Get the payment status
            ReplyMessage reply = SubmitTransaction( financialGateway, verifyRequest, out errorMessage );
            if ( reply != null && reply.reasonCode.Equals( GATEWAY_RESPONSE_SUCCESS ) )
            {
                transaction.IsActive = reply.paySubscriptionRetrieveReply.status.ToUpper() == "CURRENT";
                var startDate = GetDate( reply.paySubscriptionRetrieveReply.startDate );
                transaction.StartDate = startDate ?? transaction.StartDate;
                transaction.NextPaymentDate = NextPaymentDate( startDate, reply.paySubscriptionRetrieveReply.frequency ) ?? transaction.NextPaymentDate;
                transaction.NumberOfPayments = reply.paySubscriptionRetrieveReply.totalPayments.AsIntegerOrNull() ?? transaction.NumberOfPayments;
                transaction.LastStatusUpdateDateTime = DateTime.Now;
                return true;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = ProcessError( reply );
            }

            return false;
        }

        /// <summary>
        /// Gets the payments that have been processed for any scheduled transactions
        /// </summary>
        /// <param name="startDate">The start date.</param>
        /// <param name="endDate">The end date.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override List<Payment> GetPayments( FinancialGateway financialGateway, DateTime startDate, DateTime endDate, out string errorMessage )
        {
            errorMessage = string.Empty;
            List<Payment> paymentList = new List<Payment>();
            var reportParams = new Dictionary<string, string>();
            var reportingApi = new Reporting.Api(
                GetAttributeValue( financialGateway, "MerchantID" ),
                GetAttributeValue( financialGateway, "TransactionKey" ),
                GetAttributeValue( financialGateway, "ReportUser" ),
                GetAttributeValue( financialGateway, "ReportPassword" ),
                GetAttributeValue( financialGateway, "Mode" ).Equals( "Live", StringComparison.CurrentCultureIgnoreCase )
            );

            TimeSpan timeDifference = endDate - startDate;
            for ( int offset = 0; offset <= timeDifference.TotalDays; offset++ )
            {
                DateTime offsetDate = startDate.AddDays( offset ) < endDate ? startDate.AddDays( offset ) : endDate;
                reportParams.Add( "date", offsetDate.ToString( "yyyy/MM/dd" ) );

                DataTable dt = reportingApi.GetReport( "SubscriptionDetailReport", reportParams, out errorMessage );
                if ( dt != null && dt.Rows.Count > 0 )
                {
                    foreach ( DataRow row in dt.Rows )
                    {
                        var payment = new Payment();

                        decimal amount = decimal.MinValue;
                        payment.Amount = decimal.TryParse( row["Amount"].ToString(), out amount ) ? amount : 0.0M;

                        var time = DateTime.MinValue;
                        payment.TransactionDateTime = DateTime.TryParse( row["Time"].ToString(), out time ) ? time : DateTime.MinValue;

                        payment.TransactionCode = row["Code"].ToString();
                        payment.GatewayScheduleId = row["Schedule"].ToString();
                        payment.ScheduleActive = row["Status"].ToString() == "CURRENT";
                        paymentList.Add( payment );
                    }
                }

                reportParams.Clear();
            }

            if ( paymentList.Any() )
            {
                return paymentList;
            }
            else
            {
                errorMessage = "The subscription detail report did not return any data for the timeframe";
                return null;
            }
        }

        /// <summary>
        /// Gets the reference number from the gateway for converting a transaction to a billing profile.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override string GetReferenceNumber( FinancialTransaction transaction, out string errorMessage )
        {
            errorMessage = string.Empty;
            var financialGateway = transaction.FinancialGateway;
            RequestMessage request = GetMerchantInfo( financialGateway );
            request.billTo = GetBillTo( transaction );
            request.recurringSubscriptionInfo = new RecurringSubscriptionInfo();
            request.recurringSubscriptionInfo.frequency = "ON-DEMAND";
            request.recurringSubscriptionInfo.amount = "0";
            request.paySubscriptionCreateService = new PaySubscriptionCreateService();
            request.paySubscriptionCreateService.run = "true";
            request.paySubscriptionCreateService.paymentRequestID = transaction.TransactionCode;

            // Get the reference code
            ReplyMessage reply = SubmitTransaction( financialGateway, request, out errorMessage );
            if ( reply != null && reply.reasonCode == GATEWAY_RESPONSE_SUCCESS )
            {
                return reply.paySubscriptionCreateReply.subscriptionID;
            }
            else if ( string.IsNullOrEmpty( errorMessage ) )
            {
                errorMessage = ProcessError( reply );
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets the reference number needed to process future payments from this scheduled transaction.
        /// </summary>
        /// <param name="scheduledTransaction">The scheduled transaction.</param>
        /// <param name="errorMessage">The error message.</param>
        /// <returns></returns>
        public override string GetReferenceNumber( FinancialScheduledTransaction scheduledTransaction, out string errorMessage )
        {
            errorMessage = string.Empty;
            return scheduledTransaction.TransactionCode;
        }

        #endregion Gateway Component Implementation

        #region Process Transaction

        /// <summary>
        /// Submits the transaction.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <returns></returns>
        private ReplyMessage SubmitTransaction( FinancialGateway financialGateway, RequestMessage request, out string errorMessage )
        {
            ReplyMessage reply = new ReplyMessage();
            TransactionProcessorClient client = GetProxyClient( financialGateway, out errorMessage );

            // Error message already set, return
            if ( client == null )
            {
                return null;
            }

            try
            {
                reply = client.runTransaction( request );
                return reply;
            }
            catch ( TimeoutException e )
            {
                reply.reasonCode = "151";
                reply.additionalData = e.ToString();
                return reply;
            }
            catch ( FaultException e )
            {
                reply.reasonCode = "150";
                reply.additionalData = e.ToString();
                return reply;
            }
            catch ( Exception e )
            {
                reply.reasonCode = "";
                reply.additionalData = e.ToString();
                return reply;
            }
        }

        /// <summary>
        /// Processes the error message.
        /// </summary>
        /// <param name="reply">The reply.</param>
        /// <returns></returns>
        private string ProcessError( ReplyMessage reply )
        {
            // Make sure the reply is valid
            if ( reply == null )
            {
                return "Invalid response from the paymnent gateway.";
            }

            int reasonCode = int.Parse( reply.reasonCode );
            switch ( reasonCode )
            {
                // Missing field or fields
                case 101:
                    return "\nThe following required fields are missing: " + string.Join( "\n", reply.missingField ?? new string[0] );
                // Invalid field or fields
                case 102:
                    return "\nThe following fields are invalid: " + string.Join( "\n", reply.invalidField ?? new string[0] );
                // Partial payment approved
                case 110:
                    return "\nOnly a partial amount of this transaction was approved.";
                // General system failure
                case 150:
                    return string.Format( "\nThe payment processor did not process your payment. {0}", reply.additionalData );
                // System timeout
                case 151:
                    return string.Format( "\nThe payment request timed out. {0}", reply.additionalData ); ;
                // Service request timed out
                case 152:
                    return "\nThe payment service timed out.";
                // AVS check failed
                case 200:
                    return "\nThe payment billing address did not match the bank's address on record. Please verify your address details.";
                // Expired card
                case 202:
                    return "\nThe card has expired. Please use a different card or select another form of payment.";
                // Card declined
                case 203:
                    return "\nThe card was declined without a reason given. Please use a different card or select another form of payment.";
                // Insufficient funds
                case 204:
                    return "\nInsufficient funds in the account. Please use another form of payment.";
                // Stolen card
                case 205:
                    return "\nThe card has been reported stolen. Please use a different card or select another form of payment.";
                // Bank unavailable
                case 207:
                    return "\nThe bank processor is temporarily unavailable. Please try again in a few minutes.";
                // Card not active
                case 208:
                    return "\nThe card is inactive or not authorized for internet transactions. Please use a different card or select another form of payment.";
                // AmEx invalid CID
                case 209:
                    return "\nThe card identification digit did not match.  Please use a different card or select another form of payment.";
                // Maxed out
                case 210:
                    return "\nThe card has reached its credit limit.  Please use a different card or select another form of payment.";
                // Invalid verification #
                case 211:
                    return "\nThe card verification number is invalid. Please verify your 3 or 4 digit verification number.";
                // Frozen account
                case 222:
                    return "\nThe selected account has been frozen. Please use another form of payment.";
                // Invalid verification #
                case 230:
                    return "\nThe card verification number is invalid. Please verify your 3 or 4 digit verification number.";
                // Invalid account #
                case 231:
                    return "\nThe account number is invalid. Please use another form of payment.";
                // Invalid merchant config
                case 234:
                    return "\nThe merchant configuration is invalid. Please contact CyberSource customer support.";
                // Processor failure
                case 236:
                    return "\nThe payment processor is offline. Please try again in a few minutes.";
                // Card type not accepted
                case 240:
                    return "\nThe card type is not accepted by the merchant. Please use another form of payment.";
                // Payment processor timeout
                case 250:
                    return "\nThe payment request was received but has not yet been processed.";
                // Any others not identified
                default:
                    return string.Format( "\nYour payment was not processed. {0}", reply.additionalData );
            }
        }

        #endregion Process Transaction

        #region Helper Methods

        /// <summary>
        /// Gets the merchant information.
        /// </summary>
        /// <returns></returns>
        private RequestMessage GetMerchantInfo( FinancialGateway financialGateway, bool includeEnvironment = true )
        {
            if ( financialGateway.Attributes == null )
            {
                financialGateway.LoadAttributes();
            }

            RequestMessage request = new RequestMessage();
            request.merchantID = GetAttributeValue( financialGateway, "MerchantID" );
            request.merchantReferenceCode = Guid.NewGuid().ToString();

            if ( includeEnvironment )
            {
                request.clientLibraryVersion = Environment.Version.ToString();
                request.clientApplication = VersionInfo.GetRockProductVersionFullName();
                request.clientApplicationVersion = VersionInfo.GetRockProductVersionNumber();
                request.clientApplicationUser = GetAttributeValue( financialGateway, "OrganizationName" );
                request.clientEnvironment =
                    Environment.OSVersion.Platform +
                    Environment.OSVersion.Version.ToString() + "-CLR" +
                    Environment.Version.ToString();
            }

            return request;
        }

        /// <summary>
        /// Gets the proxy client.
        /// </summary>
        /// <param name="financialGateway">The financial gateway.</param>
        /// <returns></returns>
        private TransactionProcessorClient GetProxyClient( FinancialGateway financialGateway, out string errorMessage )
        {
            errorMessage = string.Empty;
            string merchantID = GetAttributeValue( financialGateway, "MerchantID" );
            string transactionkey = GetAttributeValue( financialGateway, "TransactionKey" );

            string gatewayEndpoint = null;
            if ( GetAttributeValue( financialGateway, "Mode" ).Equals( "Live", StringComparison.CurrentCultureIgnoreCase ) )
            {
                gatewayEndpoint = GetAttributeValue( financialGateway, "LiveGatewayURL" );
            }
            else
            {
                gatewayEndpoint = GetAttributeValue( financialGateway, "TestGatewayURL" );
            }

            EndpointAddress address = null;
            if ( !string.IsNullOrEmpty( gatewayEndpoint ) )
            {
                gatewayEndpoint = gatewayEndpoint.EnsureTrailingForwardslash();
                address = new EndpointAddress( string.Format( "{0}CyberSourceTransaction_{1}", gatewayEndpoint, GATEWAY_VERSION ) );
            }
            else
            {
                errorMessage = "Financial gateway is not configured with a valid endpoint.";
                return null;
            }

            BasicHttpBinding binding = new BasicHttpBinding();
            binding.Name = "ITransactionProcessor";
            binding.MaxBufferSize = 2147483647;
            binding.MaxBufferPoolSize = 2147483647;
            binding.MaxReceivedMessageSize = 2147483647;
            binding.ReaderQuotas.MaxDepth = 2147483647;
            binding.ReaderQuotas.MaxArrayLength = 2147483647;
            binding.ReaderQuotas.MaxBytesPerRead = 2147483647;
            binding.ReaderQuotas.MaxStringContentLength = 2147483647;
            binding.Security.Mode = BasicHttpSecurityMode.TransportWithMessageCredential;

            var proxy = new TransactionProcessorClient( binding, address );
            proxy.ClientCredentials.UserName.UserName = merchantID;
            proxy.ClientCredentials.UserName.Password = transactionkey;
            proxy.Endpoint.Address = address;
            proxy.Endpoint.Binding = binding;
            return proxy;
        }

        /// <summary>
        /// Gets the payment information.
        /// </summary>
        /// <returns></returns>
        private RequestMessage GetPaymentInfo( FinancialGateway financialGateway, PaymentInfo paymentInfo )
        {
            RequestMessage request = GetMerchantInfo( financialGateway );

            if ( paymentInfo is CreditCardPaymentInfo )
            {
                var cc = paymentInfo as CreditCardPaymentInfo;
                request.card = GetCard( cc );
            }
            else if ( paymentInfo is ACHPaymentInfo )
            {
                var ach = paymentInfo as ACHPaymentInfo;
                request.check = GetCheck( ach );
            }
            else if ( paymentInfo is ReferencePaymentInfo )
            {
                var reference = paymentInfo as ReferencePaymentInfo;
                request.recurringSubscriptionInfo = new RecurringSubscriptionInfo();
                request.recurringSubscriptionInfo.subscriptionID = reference.ReferenceNumber;
            }
            else
            {
                return null;
            }

            return request;
        }

        /// <summary>
        /// Gets the billing details from user submitted payment info.
        /// </summary>
        /// <param name="paymentInfo">The payment information.</param>
        /// <returns></returns>
        private BillTo GetBillTo( PaymentInfo paymentInfo )
        {
            BillTo billingInfo = new BillTo();

            if ( paymentInfo.Phone == null )
            {
                paymentInfo.Phone = string.Empty;
            }

            if ( paymentInfo is CreditCardPaymentInfo )
            {
                var cc = paymentInfo as CreditCardPaymentInfo;
                billingInfo.street1 = cc.BillingStreet1.Left( 50 );
                billingInfo.city = cc.BillingCity.Left( 50 );
                billingInfo.state = cc.BillingState.Left( 2 );
                billingInfo.postalCode = cc.BillingPostalCode.Left( 10 );
            }
            else
            {
                billingInfo.street1 = paymentInfo.Street1.Left( 50 );           // up to 50 chars
                billingInfo.city = paymentInfo.City.Left( 50 );                 // up to 50 chars
                billingInfo.state = paymentInfo.State.Left( 2 );                // only 2 chars

                var zip = paymentInfo.PostalCode;
                if ( !string.IsNullOrWhiteSpace( zip ) && zip.Length > 5 )
                {
                    Regex.Replace( zip, @"^(.{5})(.{4})$", "$1-$2" );           // up to 9 chars with a separating -
                }
                billingInfo.postalCode = zip;
            }

            billingInfo.firstName = paymentInfo.FirstName.Left( 50 );       // up to 50 chars
            billingInfo.lastName = paymentInfo.LastName.Left( 60 );         // up to 60 chars
            billingInfo.email = paymentInfo.Email;                          // up to 255 chars
            billingInfo.phoneNumber = paymentInfo.Phone.Left( 15 );         // up to 15 chars

            var country = paymentInfo.Country ?? "US";
            billingInfo.country = country.Left( 2 );                        // only 2 chars

            var ipAddr = Dns.GetHostEntry( Dns.GetHostName() ).AddressList
                .FirstOrDefault( ip => ip.AddressFamily == AddressFamily.InterNetwork );
            billingInfo.ipAddress = ipAddr.ToString();                      // machine IP address

            return billingInfo;
        }

        /// <summary>
        /// Gets the bill to from a transaction.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <returns></returns>
        private BillTo GetBillTo( FinancialTransaction transaction )
        {
            BillTo billingInfo = new BillTo();
            billingInfo.customerID = transaction.AuthorizedPersonAlias.Id.ToString();
            billingInfo.firstName = transaction.AuthorizedPersonAlias.Person.FirstName.Left( 50 );       // up to 50 chars
            billingInfo.lastName = transaction.AuthorizedPersonAlias.Person.LastName.Left( 50 );         // up to 60 chars
            billingInfo.email = transaction.AuthorizedPersonAlias.Person.Email.Left( 255 );              // up to 255 chars
            billingInfo.ipAddress = Dns.GetHostEntry( Dns.GetHostName() )
                .AddressList.FirstOrDefault( ip => ip.AddressFamily == AddressFamily.InterNetwork ).ToString();

            return billingInfo;
        }

        /// <summary>
        /// Gets the total payment item.
        /// </summary>
        /// <param name="paymentInfo">The payment information.</param>
        /// <returns></returns>
        private Item[] GetItems( PaymentInfo paymentInfo )
        {
            List<Item> itemList = new List<Item>();

            Item item = new Item();
            item.id = "0";
            item.unitPrice = paymentInfo.Amount.ToString();
            item.totalAmount = paymentInfo.Amount.ToString();
            itemList.Add( item );
            return itemList.ToArray();
        }

        /// <summary>
        /// Gets the purchase totals.
        /// </summary>
        /// <returns></returns>
        private PurchaseTotals GetTotals()
        {
            // paymentInfo not used here since fixed payment installments not implemented
            PurchaseTotals purchaseTotals = new PurchaseTotals();
            purchaseTotals.currency = "USD";
            return purchaseTotals;
        }

        /// <summary>
        /// Gets the card information.
        /// </summary>
        /// <param name="paymentInfo">The payment information.</param>
        /// <returns></returns>
        private Card GetCard( CreditCardPaymentInfo cc )
        {
            var card = new Card();
            card.accountNumber = cc.Number.AsNumeric();
            card.expirationMonth = cc.ExpirationDate.Month.ToString( "D2" );
            card.expirationYear = cc.ExpirationDate.Year.ToString( "D4" );
            card.cvNumber = cc.Code.AsNumeric();
            card.cvIndicator = "1";

            if ( cc.CreditCardTypeValue != null )
            {
                switch ( cc.CreditCardTypeValue.Value )
                {
                    case "Visa":
                        card.cardType = "001";
                        break;

                    case "MasterCard":
                        card.cardType = "002";
                        break;

                    case "American Express":
                        card.cardType = "003";
                        break;

                    case "Discover":
                        card.cardType = "004";
                        break;

                    case "Diners":
                        card.cardType = "005";
                        break;

                    case "Carte Blanche":
                        card.cardType = "006";
                        break;

                    case "JCB":
                        card.cardType = "007";
                        break;

                    default:
                        card.cardType = string.Empty;
                        break;
                }
            }

            return card;
        }

        /// <summary>
        /// Gets the check information.
        /// </summary>
        /// <param name="paymentInfo">The payment information.</param>
        /// <returns></returns>
        private Check GetCheck( ACHPaymentInfo ach )
        {
            var check = new Check();
            check.accountNumber = ach.BankAccountNumber.AsNumeric();
            check.accountType = ach.AccountType == BankAccountType.Checking ? "C" : "S";
            check.bankTransitNumber = ach.BankRoutingNumber.AsNumeric();
            check.secCode = "WEB";

            return check;
        }

        /// <summary>
        /// Gets the payment start date.
        /// </summary>
        /// <param name="schedule">The schedule.</param>
        /// <returns></returns>
        private string GetStartDate( PaymentSchedule schedule )
        {
            string startDate = string.Empty;

            if ( !schedule.TransactionFrequencyValue.Guid.ToString().Equals( Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_TWICEMONTHLY, StringComparison.InvariantCultureIgnoreCase ) )
            {
                startDate = schedule.StartDate.ToString( "yyyyMMdd" );
            }
            else
            {
                // determine the next valid day on a twice monthly schedule;
                // today's date is not a valid option (enforced in UI)
                var dateOffset = schedule.StartDate.AddDays( -1 );
                if ( dateOffset.Day >= 15 )
                {
                    dateOffset = new DateTime( dateOffset.Year, dateOffset.Month, 1 ).AddMonths( 1 );
                }
                else
                {
                    dateOffset = new DateTime( dateOffset.Year, dateOffset.Month, 15 );
                }

                startDate = dateOffset.ToString( "yyyyMMdd" );
            }

            return startDate;
        }

        /// <summary>
        /// Gets the payment frequency.
        /// </summary>
        /// <param name="schedule">The schedule.</param>
        /// <returns></returns>
        private string GetFrequency( PaymentSchedule schedule )
        {
            string frequency = string.Empty;

            var selectedFrequencyGuid = schedule.TransactionFrequencyValue.Guid.ToString().ToUpper();
            switch ( selectedFrequencyGuid )
            {
                case Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_ONE_TIME:
                    frequency = "ON-DEMAND";
                    break;

                case Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_WEEKLY:
                    frequency = "WEEKLY";
                    break;

                case Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_BIWEEKLY:
                    frequency = "BI-WEEKLY";
                    break;

                case Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_TWICEMONTHLY:
                    frequency = "SEMI-MONTHLY";
                    break;

                case Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_MONTHLY:
                    frequency = "MONTHLY";
                    break;

                case Rock.SystemGuid.DefinedValue.TRANSACTION_FREQUENCY_YEARLY:
                    frequency = "ANNUALLY";
                    break;
            }

            return frequency;
        }

        /// <summary>
        /// Gets the next payment date.
        /// </summary>
        /// <param name="date">The date.</param>
        /// <param name="frequency">The frequency.</param>
        /// <returns></returns>
        private DateTime? NextPaymentDate( DateTime? dt, string frequency )
        {
            DateTime startDate = (DateTime)( dt ?? DateTime.Now );
            DateTime nextDate;
            switch ( frequency.ToUpper() )
            {
                case "WEEKLY":
                    nextDate = startDate.AddDays( 7 );
                    break;

                case "BI-WEEKLY":
                    nextDate = startDate.AddDays( 14 );
                    break;

                case "SEMI-MONTHLY":
                    nextDate = startDate.Day >= 15
                        ? new DateTime( startDate.Year, startDate.Month, 1 ).AddMonths( 1 )
                        : new DateTime( startDate.Year, startDate.Month, 15 );
                    break;

                case "MONTHLY":
                    nextDate = startDate.AddMonths( 1 );
                    break;

                case "ANNUALLY":
                    nextDate = startDate.AddYears( 1 );
                    break;

                default:
                    nextDate = startDate;
                    break;
            }

            return nextDate;
        }

        /// <summary>
        /// Gets the date.
        /// </summary>
        /// <param name="date">The date.</param>
        /// <returns></returns>
        private DateTime? GetDate( string date )
        {
            DateTime dt = DateTime.MinValue;
            if ( DateTime.TryParseExact( date, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out dt ) )
            {
                return dt;
            }
            else
            {
                return null;
            }
        }

        #endregion Helper Methods
    }
}