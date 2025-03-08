using System;
using System.Data;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Radzen;

using SQLFlowUi.Data;

namespace SQLFlowUi
{
    public partial class sqlflowProdService
    {
        sqlflowProdContext Context
        {
           get
           {
             return this.context;
           }
        }

        private readonly sqlflowProdContext context;
        private readonly NavigationManager navigationManager;

        public sqlflowProdService(sqlflowProdContext context, NavigationManager navigationManager)
        {
            this.context = context;
            this.navigationManager = navigationManager;
        }

        public void Reset() => Context.ChangeTracker.Entries().Where(e => e.Entity != null).ToList().ForEach(e => e.State = EntityState.Detached);

        public void ApplyQuery<T>(ref IQueryable<T> items, Query query = null)
        {
            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Filter))
                {
                    try
                    {
                        // Create a custom parsing config with available properties
                        var config = new ParsingConfig
                        {
                            ResolveTypesBySimpleName = true
                            // NullPropagation is not available in your version
                        };

                        // Apply preprocessing to handle ToString() safely
                        var safeFilter = PreprocessDynamicFilter(query.Filter);

                        if (query.FilterParameters != null)
                        {
                            items = items.Where(config, safeFilter, query.FilterParameters);
                        }
                        else
                        {
                            items = items.Where(config, safeFilter);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in dynamic query filter: {ex.Message}");
                    }
                }
                // Rest of your code remains the same
                if (!string.IsNullOrEmpty(query.OrderBy))
                {
                    items = items.OrderBy(query.OrderBy);
                }
                if (query.Skip.HasValue)
                {
                    items = items.Skip(query.Skip.Value);
                }
                if (query.Top.HasValue)
                {
                    items = items.Take(query.Top.Value);
                }
            }
        }

        private string PreprocessDynamicFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return filter;

            // Enhanced regex to capture more complex property paths
            // This will handle nested properties like "a.b.c.ToString()"
            return System.Text.RegularExpressions.Regex.Replace(
                filter,
                @"([\w\.\[\]]+)\.ToString\(\)",
                "($1 != null ? $1.ToString() : string.Empty)"
            );
        }
         
        public async Task ExportAssertionToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/assertion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/assertion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportAssertionToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/assertion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/assertion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnAssertionRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Assertion> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.Assertion>> GetAssertion(Query query = null)
        {
            var items = Context.Assertion.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnAssertionRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnAssertionGet(SQLFlowUi.Models.sqlflowProd.Assertion item);
        partial void OnGetAssertionByAssertionId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Assertion> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.Assertion> GetAssertionByAssertionId(int assertionid)
        {
            var items = Context.Assertion
                              .AsNoTracking()
                              .Where(i => i.AssertionID == assertionid);

 
            OnGetAssertionByAssertionId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnAssertionGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnAssertionCreated(SQLFlowUi.Models.sqlflowProd.Assertion item);
        partial void OnAfterAssertionCreated(SQLFlowUi.Models.sqlflowProd.Assertion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Assertion> CreateAssertion(SQLFlowUi.Models.sqlflowProd.Assertion assertion)
        {
            OnAssertionCreated(assertion);

            var existingItem = Context.Assertion
                              .Where(i => i.AssertionID == assertion.AssertionID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.Assertion.Add(assertion);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(assertion).State = EntityState.Detached;
                throw;
            }

            OnAfterAssertionCreated(assertion);

            return assertion;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.Assertion> CancelAssertionChanges(SQLFlowUi.Models.sqlflowProd.Assertion item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnAssertionUpdated(SQLFlowUi.Models.sqlflowProd.Assertion item);
        partial void OnAfterAssertionUpdated(SQLFlowUi.Models.sqlflowProd.Assertion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Assertion> UpdateAssertion(int assertionid, SQLFlowUi.Models.sqlflowProd.Assertion assertion)
        {
            OnAssertionUpdated(assertion);

            var itemToUpdate = Context.Assertion
                              .Where(i => i.AssertionID == assertion.AssertionID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(assertion);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterAssertionUpdated(assertion);

            return assertion;
        }

        partial void OnAssertionDeleted(SQLFlowUi.Models.sqlflowProd.Assertion item);
        partial void OnAfterAssertionDeleted(SQLFlowUi.Models.sqlflowProd.Assertion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Assertion> DeleteAssertion(int assertionid)
        {
            var itemToDelete = Context.Assertion
                              .Where(i => i.AssertionID == assertionid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnAssertionDeleted(itemToDelete);


            Context.Assertion.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterAssertionDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportDataSubscriberToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/datasubscriber/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/datasubscriber/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportDataSubscriberToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/datasubscriber/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/datasubscriber/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnDataSubscriberRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.DataSubscriber> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.DataSubscriber>> GetDataSubscriber(Query query = null)
        {
            var items = Context.DataSubscriber.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnDataSubscriberRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnDataSubscriberGet(SQLFlowUi.Models.sqlflowProd.DataSubscriber item);
        partial void OnGetDataSubscriberByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.DataSubscriber> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriber> GetDataSubscriberByFlowId(int flowid)
        {
            var items = Context.DataSubscriber
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetDataSubscriberByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnDataSubscriberGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnDataSubscriberCreated(SQLFlowUi.Models.sqlflowProd.DataSubscriber item);
        partial void OnAfterDataSubscriberCreated(SQLFlowUi.Models.sqlflowProd.DataSubscriber item);

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriber> CreateDataSubscriber(SQLFlowUi.Models.sqlflowProd.DataSubscriber datasubscriber)
        {
            OnDataSubscriberCreated(datasubscriber);

            var existingItem = Context.DataSubscriber
                              .Where(i => i.FlowID == datasubscriber.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.DataSubscriber.Add(datasubscriber);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(datasubscriber).State = EntityState.Detached;
                throw;
            }

            OnAfterDataSubscriberCreated(datasubscriber);

            return datasubscriber;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriber> CancelDataSubscriberChanges(SQLFlowUi.Models.sqlflowProd.DataSubscriber item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnDataSubscriberUpdated(SQLFlowUi.Models.sqlflowProd.DataSubscriber item);
        partial void OnAfterDataSubscriberUpdated(SQLFlowUi.Models.sqlflowProd.DataSubscriber item);

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriber> UpdateDataSubscriber(int flowid, SQLFlowUi.Models.sqlflowProd.DataSubscriber datasubscriber)
        {
            OnDataSubscriberUpdated(datasubscriber);

            var itemToUpdate = Context.DataSubscriber
                              .Where(i => i.FlowID == datasubscriber.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(datasubscriber);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterDataSubscriberUpdated(datasubscriber);

            return datasubscriber;
        }

        partial void OnDataSubscriberDeleted(SQLFlowUi.Models.sqlflowProd.DataSubscriber item);
        partial void OnAfterDataSubscriberDeleted(SQLFlowUi.Models.sqlflowProd.DataSubscriber item);

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriber> DeleteDataSubscriber(int flowid)
        {
            var itemToDelete = Context.DataSubscriber
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnDataSubscriberDeleted(itemToDelete);


            Context.DataSubscriber.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterDataSubscriberDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportDataSubscriberQueryToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/datasubscriberquery/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/datasubscriberquery/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportDataSubscriberQueryToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/datasubscriberquery/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/datasubscriberquery/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnDataSubscriberQueryRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery>> GetDataSubscriberQuery(Query query = null)
        {
            var items = Context.DataSubscriberQuery.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnDataSubscriberQueryRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnDataSubscriberQueryGet(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item);
        partial void OnGetDataSubscriberQueryByQueryId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> GetDataSubscriberQueryByQueryId(int queryid)
        {
            var items = Context.DataSubscriberQuery
                              .AsNoTracking()
                              .Where(i => i.QueryID == queryid);

 
            OnGetDataSubscriberQueryByQueryId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnDataSubscriberQueryGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnDataSubscriberQueryCreated(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item);
        partial void OnAfterDataSubscriberQueryCreated(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item);

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> CreateDataSubscriberQuery(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery datasubscriberquery)
        {
            OnDataSubscriberQueryCreated(datasubscriberquery);

            var existingItem = Context.DataSubscriberQuery
                              .Where(i => i.QueryID == datasubscriberquery.QueryID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.DataSubscriberQuery.Add(datasubscriberquery);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(datasubscriberquery).State = EntityState.Detached;
                throw;
            }

            OnAfterDataSubscriberQueryCreated(datasubscriberquery);

            return datasubscriberquery;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> CancelDataSubscriberQueryChanges(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnDataSubscriberQueryUpdated(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item);
        partial void OnAfterDataSubscriberQueryUpdated(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item);

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> UpdateDataSubscriberQuery(int queryid, SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery datasubscriberquery)
        {
            OnDataSubscriberQueryUpdated(datasubscriberquery);

            var itemToUpdate = Context.DataSubscriberQuery
                              .Where(i => i.QueryID == datasubscriberquery.QueryID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(datasubscriberquery);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterDataSubscriberQueryUpdated(datasubscriberquery);

            return datasubscriberquery;
        }

        partial void OnDataSubscriberQueryDeleted(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item);
        partial void OnAfterDataSubscriberQueryDeleted(SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery item);

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> DeleteDataSubscriberQuery(int queryid)
        {
            var itemToDelete = Context.DataSubscriberQuery
                              .Where(i => i.QueryID == queryid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnDataSubscriberQueryDeleted(itemToDelete);


            Context.DataSubscriberQuery.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterDataSubscriberQueryDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportExportToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/export/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/export/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportExportToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/export/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/export/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnExportRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Export> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.Export>> GetExport(Query query = null)
        {
            var items = Context.Export.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnExportRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnExportGet(SQLFlowUi.Models.sqlflowProd.Export item);
        partial void OnGetExportByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Export> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.Export> GetExportByFlowId(int flowid)
        {
            var items = Context.Export
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetExportByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnExportGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnExportCreated(SQLFlowUi.Models.sqlflowProd.Export item);
        partial void OnAfterExportCreated(SQLFlowUi.Models.sqlflowProd.Export item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Export> CreateExport(SQLFlowUi.Models.sqlflowProd.Export export)
        {
            OnExportCreated(export);

            var existingItem = Context.Export
                              .Where(i => i.FlowID == export.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.Export.Add(export);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(export).State = EntityState.Detached;
                throw;
            }

            OnAfterExportCreated(export);

            return export;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.Export> CancelExportChanges(SQLFlowUi.Models.sqlflowProd.Export item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnExportUpdated(SQLFlowUi.Models.sqlflowProd.Export item);
        partial void OnAfterExportUpdated(SQLFlowUi.Models.sqlflowProd.Export item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Export> UpdateExport(int flowid, SQLFlowUi.Models.sqlflowProd.Export export)
        {
            OnExportUpdated(export);

            var itemToUpdate = Context.Export
                              .Where(i => i.FlowID == export.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(export);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterExportUpdated(export);

            return export;
        }

        partial void OnExportDeleted(SQLFlowUi.Models.sqlflowProd.Export item);
        partial void OnAfterExportDeleted(SQLFlowUi.Models.sqlflowProd.Export item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Export> DeleteExport(int flowid)
        {
            var itemToDelete = Context.Export
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnExportDeleted(itemToDelete);


            Context.Export.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterExportDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportGeoCodingToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/geocoding/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/geocoding/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportGeoCodingToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/geocoding/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/geocoding/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnGeoCodingRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.GeoCoding> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.GeoCoding>> GetGeoCoding(Query query = null)
        {
            var items = Context.GeoCoding.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnGeoCodingRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnGeoCodingGet(SQLFlowUi.Models.sqlflowProd.GeoCoding item);
        partial void OnGetGeoCodingByGeoCodingId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.GeoCoding> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.GeoCoding> GetGeoCodingByGeoCodingId(int geocodingid)
        {
            var items = Context.GeoCoding
                              .AsNoTracking()
                              .Where(i => i.GeoCodingID == geocodingid);

 
            OnGetGeoCodingByGeoCodingId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnGeoCodingGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnGeoCodingCreated(SQLFlowUi.Models.sqlflowProd.GeoCoding item);
        partial void OnAfterGeoCodingCreated(SQLFlowUi.Models.sqlflowProd.GeoCoding item);

        public async Task<SQLFlowUi.Models.sqlflowProd.GeoCoding> CreateGeoCoding(SQLFlowUi.Models.sqlflowProd.GeoCoding geocoding)
        {
            OnGeoCodingCreated(geocoding);

            var existingItem = Context.GeoCoding
                              .Where(i => i.GeoCodingID == geocoding.GeoCodingID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.GeoCoding.Add(geocoding);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(geocoding).State = EntityState.Detached;
                throw;
            }

            OnAfterGeoCodingCreated(geocoding);

            return geocoding;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.GeoCoding> CancelGeoCodingChanges(SQLFlowUi.Models.sqlflowProd.GeoCoding item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnGeoCodingUpdated(SQLFlowUi.Models.sqlflowProd.GeoCoding item);
        partial void OnAfterGeoCodingUpdated(SQLFlowUi.Models.sqlflowProd.GeoCoding item);

        public async Task<SQLFlowUi.Models.sqlflowProd.GeoCoding> UpdateGeoCoding(int geocodingid, SQLFlowUi.Models.sqlflowProd.GeoCoding geocoding)
        {
            OnGeoCodingUpdated(geocoding);

            var itemToUpdate = Context.GeoCoding
                              .Where(i => i.GeoCodingID == geocoding.GeoCodingID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(geocoding);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterGeoCodingUpdated(geocoding);

            return geocoding;
        }

        partial void OnGeoCodingDeleted(SQLFlowUi.Models.sqlflowProd.GeoCoding item);
        partial void OnAfterGeoCodingDeleted(SQLFlowUi.Models.sqlflowProd.GeoCoding item);

        public async Task<SQLFlowUi.Models.sqlflowProd.GeoCoding> DeleteGeoCoding(int geocodingid)
        {
            var itemToDelete = Context.GeoCoding
                              .Where(i => i.GeoCodingID == geocodingid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnGeoCodingDeleted(itemToDelete);


            Context.GeoCoding.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterGeoCodingDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportHealthCheckToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/healthcheck/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/healthcheck/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportHealthCheckToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/healthcheck/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/healthcheck/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnHealthCheckRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.HealthCheck> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.HealthCheck>> GetHealthCheck(Query query = null)
        {
            var items = Context.HealthCheck.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnHealthCheckRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnHealthCheckGet(SQLFlowUi.Models.sqlflowProd.HealthCheck item);
        partial void OnGetHealthCheckByHealthCheckId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.HealthCheck> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.HealthCheck> GetHealthCheckByHealthCheckId(int healthcheckid)
        {
            var items = Context.HealthCheck
                              .AsNoTracking()
                              .Where(i => i.HealthCheckID == healthcheckid);

 
            OnGetHealthCheckByHealthCheckId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnHealthCheckGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnHealthCheckCreated(SQLFlowUi.Models.sqlflowProd.HealthCheck item);
        partial void OnAfterHealthCheckCreated(SQLFlowUi.Models.sqlflowProd.HealthCheck item);

        public async Task<SQLFlowUi.Models.sqlflowProd.HealthCheck> CreateHealthCheck(SQLFlowUi.Models.sqlflowProd.HealthCheck healthcheck)
        {
            OnHealthCheckCreated(healthcheck);

            var existingItem = Context.HealthCheck
                              .Where(i => i.HealthCheckID == healthcheck.HealthCheckID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.HealthCheck.Add(healthcheck);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(healthcheck).State = EntityState.Detached;
                throw;
            }

            OnAfterHealthCheckCreated(healthcheck);

            return healthcheck;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.HealthCheck> CancelHealthCheckChanges(SQLFlowUi.Models.sqlflowProd.HealthCheck item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnHealthCheckUpdated(SQLFlowUi.Models.sqlflowProd.HealthCheck item);
        partial void OnAfterHealthCheckUpdated(SQLFlowUi.Models.sqlflowProd.HealthCheck item);

        public async Task<SQLFlowUi.Models.sqlflowProd.HealthCheck> UpdateHealthCheck(int healthcheckid, SQLFlowUi.Models.sqlflowProd.HealthCheck healthcheck)
        {
            OnHealthCheckUpdated(healthcheck);

            var itemToUpdate = Context.HealthCheck
                              .Where(i => i.HealthCheckID == healthcheck.HealthCheckID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(healthcheck);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterHealthCheckUpdated(healthcheck);

            return healthcheck;
        }

        partial void OnHealthCheckDeleted(SQLFlowUi.Models.sqlflowProd.HealthCheck item);
        partial void OnAfterHealthCheckDeleted(SQLFlowUi.Models.sqlflowProd.HealthCheck item);

        public async Task<SQLFlowUi.Models.sqlflowProd.HealthCheck> DeleteHealthCheck(int healthcheckid)
        {
            var itemToDelete = Context.HealthCheck
                              .Where(i => i.HealthCheckID == healthcheckid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnHealthCheckDeleted(itemToDelete);


            Context.HealthCheck.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterHealthCheckDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportIngestionToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportIngestionToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnIngestionRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Ingestion> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.Ingestion>> GetIngestion(Query query = null)
        {
            var items = Context.Ingestion.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnIngestionRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnIngestionGet(SQLFlowUi.Models.sqlflowProd.Ingestion item);
        partial void OnGetIngestionByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Ingestion> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.Ingestion> GetIngestionByFlowId(int flowid)
        {
            var items = Context.Ingestion
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetIngestionByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnIngestionGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnIngestionCreated(SQLFlowUi.Models.sqlflowProd.Ingestion item);
        partial void OnAfterIngestionCreated(SQLFlowUi.Models.sqlflowProd.Ingestion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Ingestion> CreateIngestion(SQLFlowUi.Models.sqlflowProd.Ingestion ingestion)
        {
            OnIngestionCreated(ingestion);

            var existingItem = Context.Ingestion
                              .Where(i => i.FlowID == ingestion.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.Ingestion.Add(ingestion);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(ingestion).State = EntityState.Detached;
                throw;
            }

            OnAfterIngestionCreated(ingestion);

            return ingestion;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.Ingestion> CancelIngestionChanges(SQLFlowUi.Models.sqlflowProd.Ingestion item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnIngestionUpdated(SQLFlowUi.Models.sqlflowProd.Ingestion item);
        partial void OnAfterIngestionUpdated(SQLFlowUi.Models.sqlflowProd.Ingestion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Ingestion> UpdateIngestion(int flowid, SQLFlowUi.Models.sqlflowProd.Ingestion ingestion)
        {
            OnIngestionUpdated(ingestion);

            var itemToUpdate = Context.Ingestion
                              .Where(i => i.FlowID == ingestion.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(ingestion);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterIngestionUpdated(ingestion);

            return ingestion;
        }

        partial void OnIngestionDeleted(SQLFlowUi.Models.sqlflowProd.Ingestion item);
        partial void OnAfterIngestionDeleted(SQLFlowUi.Models.sqlflowProd.Ingestion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Ingestion> DeleteIngestion(int flowid)
        {
            var itemToDelete = Context.Ingestion
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnIngestionDeleted(itemToDelete);


            Context.Ingestion.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterIngestionDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportIngestionTokenExpToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestiontokenexp/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestiontokenexp/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportIngestionTokenExpToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestiontokenexp/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestiontokenexp/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnIngestionTokenExpRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp>> GetIngestionTokenExp(Query query = null)
        {
            var items = Context.IngestionTokenExp.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnIngestionTokenExpRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnIngestionTokenExpGet(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item);
        partial void OnGetIngestionTokenExpByTokenExpAlias(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> GetIngestionTokenExpByTokenExpAlias(string tokenexpalias)
        {
            var items = Context.IngestionTokenExp
                              .AsNoTracking()
                              .Where(i => i.TokenExpAlias == tokenexpalias);

 
            OnGetIngestionTokenExpByTokenExpAlias(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnIngestionTokenExpGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnIngestionTokenExpCreated(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item);
        partial void OnAfterIngestionTokenExpCreated(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> CreateIngestionTokenExp(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp ingestiontokenexp)
        {
            OnIngestionTokenExpCreated(ingestiontokenexp);

            var existingItem = Context.IngestionTokenExp
                              .Where(i => i.TokenExpAlias == ingestiontokenexp.TokenExpAlias)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.IngestionTokenExp.Add(ingestiontokenexp);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(ingestiontokenexp).State = EntityState.Detached;
                throw;
            }

            OnAfterIngestionTokenExpCreated(ingestiontokenexp);

            return ingestiontokenexp;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> CancelIngestionTokenExpChanges(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnIngestionTokenExpUpdated(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item);
        partial void OnAfterIngestionTokenExpUpdated(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> UpdateIngestionTokenExp(string tokenexpalias, SQLFlowUi.Models.sqlflowProd.IngestionTokenExp ingestiontokenexp)
        {
            OnIngestionTokenExpUpdated(ingestiontokenexp);

            var itemToUpdate = Context.IngestionTokenExp
                              .Where(i => i.TokenExpAlias == ingestiontokenexp.TokenExpAlias)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(ingestiontokenexp);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterIngestionTokenExpUpdated(ingestiontokenexp);

            return ingestiontokenexp;
        }

        partial void OnIngestionTokenExpDeleted(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item);
        partial void OnAfterIngestionTokenExpDeleted(SQLFlowUi.Models.sqlflowProd.IngestionTokenExp item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> DeleteIngestionTokenExp(string tokenexpalias)
        {
            var itemToDelete = Context.IngestionTokenExp
                              .Where(i => i.TokenExpAlias == tokenexpalias)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnIngestionTokenExpDeleted(itemToDelete);


            Context.IngestionTokenExp.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterIngestionTokenExpDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportIngestionTokenizeToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestiontokenize/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestiontokenize/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportIngestionTokenizeToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestiontokenize/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestiontokenize/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnIngestionTokenizeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTokenize>> GetIngestionTokenize(Query query = null)
        {
            var items = Context.IngestionTokenize.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnIngestionTokenizeRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnIngestionTokenizeGet(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item);
        partial void OnGetIngestionTokenizeByTokenId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> GetIngestionTokenizeByTokenId(int tokenid)
        {
            var items = Context.IngestionTokenize
                              .AsNoTracking()
                              .Where(i => i.TokenID == tokenid);

 
            OnGetIngestionTokenizeByTokenId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnIngestionTokenizeGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnIngestionTokenizeCreated(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item);
        partial void OnAfterIngestionTokenizeCreated(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> CreateIngestionTokenize(SQLFlowUi.Models.sqlflowProd.IngestionTokenize ingestiontokenize)
        {
            OnIngestionTokenizeCreated(ingestiontokenize);

            var existingItem = Context.IngestionTokenize
                              .Where(i => i.TokenID == ingestiontokenize.TokenID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.IngestionTokenize.Add(ingestiontokenize);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(ingestiontokenize).State = EntityState.Detached;
                throw;
            }

            OnAfterIngestionTokenizeCreated(ingestiontokenize);

            return ingestiontokenize;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> CancelIngestionTokenizeChanges(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnIngestionTokenizeUpdated(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item);
        partial void OnAfterIngestionTokenizeUpdated(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> UpdateIngestionTokenize(int tokenid, SQLFlowUi.Models.sqlflowProd.IngestionTokenize ingestiontokenize)
        {
            OnIngestionTokenizeUpdated(ingestiontokenize);

            var itemToUpdate = Context.IngestionTokenize
                              .Where(i => i.TokenID == ingestiontokenize.TokenID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(ingestiontokenize);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterIngestionTokenizeUpdated(ingestiontokenize);

            return ingestiontokenize;
        }

        partial void OnIngestionTokenizeDeleted(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item);
        partial void OnAfterIngestionTokenizeDeleted(SQLFlowUi.Models.sqlflowProd.IngestionTokenize item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> DeleteIngestionTokenize(int tokenid)
        {
            var itemToDelete = Context.IngestionTokenize
                              .Where(i => i.TokenID == tokenid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnIngestionTokenizeDeleted(itemToDelete);


            Context.IngestionTokenize.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterIngestionTokenizeDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportIngestionTransfromToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestiontransfrom/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestiontransfrom/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportIngestionTransfromToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestiontransfrom/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestiontransfrom/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnIngestionTransfromRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom>> GetIngestionTransfrom(Query query = null)
        {
            var items = Context.IngestionTransfrom.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnIngestionTransfromRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnIngestionTransfromGet(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item);
        partial void OnGetIngestionTransfromByTransfromId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> GetIngestionTransfromByTransfromId(int transfromid)
        {
            var items = Context.IngestionTransfrom
                              .AsNoTracking()
                              .Where(i => i.TransfromID == transfromid);

 
            OnGetIngestionTransfromByTransfromId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnIngestionTransfromGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnIngestionTransfromCreated(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item);
        partial void OnAfterIngestionTransfromCreated(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> CreateIngestionTransfrom(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom ingestiontransfrom)
        {
            OnIngestionTransfromCreated(ingestiontransfrom);

            var existingItem = Context.IngestionTransfrom
                              .Where(i => i.TransfromID == ingestiontransfrom.TransfromID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.IngestionTransfrom.Add(ingestiontransfrom);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(ingestiontransfrom).State = EntityState.Detached;
                throw;
            }

            OnAfterIngestionTransfromCreated(ingestiontransfrom);

            return ingestiontransfrom;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> CancelIngestionTransfromChanges(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnIngestionTransfromUpdated(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item);
        partial void OnAfterIngestionTransfromUpdated(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> UpdateIngestionTransfrom(int transfromid, SQLFlowUi.Models.sqlflowProd.IngestionTransfrom ingestiontransfrom)
        {
            OnIngestionTransfromUpdated(ingestiontransfrom);

            var itemToUpdate = Context.IngestionTransfrom
                              .Where(i => i.TransfromID == ingestiontransfrom.TransfromID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(ingestiontransfrom);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterIngestionTransfromUpdated(ingestiontransfrom);

            return ingestiontransfrom;
        }

        partial void OnIngestionTransfromDeleted(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item);
        partial void OnAfterIngestionTransfromDeleted(SQLFlowUi.Models.sqlflowProd.IngestionTransfrom item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> DeleteIngestionTransfrom(int transfromid)
        {
            var itemToDelete = Context.IngestionTransfrom
                              .Where(i => i.TransfromID == transfromid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnIngestionTransfromDeleted(itemToDelete);


            Context.IngestionTransfrom.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterIngestionTransfromDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportIngestionVirtualToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestionvirtual/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestionvirtual/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportIngestionVirtualToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/ingestionvirtual/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/ingestionvirtual/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnIngestionVirtualRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionVirtual>> GetIngestionVirtual(Query query = null)
        {
            var items = Context.IngestionVirtual.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnIngestionVirtualRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnIngestionVirtualGet(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item);
        partial void OnGetIngestionVirtualByVirtualId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> GetIngestionVirtualByVirtualId(int virtualid)
        {
            var items = Context.IngestionVirtual
                              .AsNoTracking()
                              .Where(i => i.VirtualID == virtualid);

 
            OnGetIngestionVirtualByVirtualId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnIngestionVirtualGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnIngestionVirtualCreated(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item);
        partial void OnAfterIngestionVirtualCreated(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> CreateIngestionVirtual(SQLFlowUi.Models.sqlflowProd.IngestionVirtual ingestionvirtual)
        {
            OnIngestionVirtualCreated(ingestionvirtual);

            var existingItem = Context.IngestionVirtual
                              .Where(i => i.VirtualID == ingestionvirtual.VirtualID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.IngestionVirtual.Add(ingestionvirtual);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(ingestionvirtual).State = EntityState.Detached;
                throw;
            }

            OnAfterIngestionVirtualCreated(ingestionvirtual);

            return ingestionvirtual;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> CancelIngestionVirtualChanges(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnIngestionVirtualUpdated(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item);
        partial void OnAfterIngestionVirtualUpdated(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> UpdateIngestionVirtual(int virtualid, SQLFlowUi.Models.sqlflowProd.IngestionVirtual ingestionvirtual)
        {
            OnIngestionVirtualUpdated(ingestionvirtual);

            var itemToUpdate = Context.IngestionVirtual
                              .Where(i => i.VirtualID == ingestionvirtual.VirtualID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(ingestionvirtual);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterIngestionVirtualUpdated(ingestionvirtual);

            return ingestionvirtual;
        }

        partial void OnIngestionVirtualDeleted(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item);
        partial void OnAfterIngestionVirtualDeleted(SQLFlowUi.Models.sqlflowProd.IngestionVirtual item);

        public async Task<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> DeleteIngestionVirtual(int virtualid)
        {
            var itemToDelete = Context.IngestionVirtual
                              .Where(i => i.VirtualID == virtualid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnIngestionVirtualDeleted(itemToDelete);


            Context.IngestionVirtual.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterIngestionVirtualDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportInvokeToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/invoke/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/invoke/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportInvokeToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/invoke/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/invoke/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnInvokeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke>> GetInvoke(Query query = null)
        {
            var items = Context.Invoke.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnInvokeRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnInvokeGet(SQLFlowUi.Models.sqlflowProd.Invoke item);
        partial void OnGetInvokeByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.Invoke> GetInvokeByFlowId(int flowid)
        {
            var items = Context.Invoke
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetInvokeByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnInvokeGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnInvokeCreated(SQLFlowUi.Models.sqlflowProd.Invoke item);
        partial void OnAfterInvokeCreated(SQLFlowUi.Models.sqlflowProd.Invoke item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Invoke> CreateInvoke(SQLFlowUi.Models.sqlflowProd.Invoke invoke)
        {
            OnInvokeCreated(invoke);

            var existingItem = Context.Invoke
                              .Where(i => i.FlowID == invoke.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.Invoke.Add(invoke);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(invoke).State = EntityState.Detached;
                throw;
            }

            OnAfterInvokeCreated(invoke);

            return invoke;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.Invoke> CancelInvokeChanges(SQLFlowUi.Models.sqlflowProd.Invoke item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnInvokeUpdated(SQLFlowUi.Models.sqlflowProd.Invoke item);
        partial void OnAfterInvokeUpdated(SQLFlowUi.Models.sqlflowProd.Invoke item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Invoke> UpdateInvoke(int flowid, SQLFlowUi.Models.sqlflowProd.Invoke invoke)
        {
            OnInvokeUpdated(invoke);

            var itemToUpdate = Context.Invoke
                              .Where(i => i.FlowID == invoke.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(invoke);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterInvokeUpdated(invoke);

            return invoke;
        }

        partial void OnInvokeDeleted(SQLFlowUi.Models.sqlflowProd.Invoke item);
        partial void OnAfterInvokeDeleted(SQLFlowUi.Models.sqlflowProd.Invoke item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Invoke> DeleteInvoke(int flowid)
        {
            var itemToDelete = Context.Invoke
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnInvokeDeleted(itemToDelete);


            Context.Invoke.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterInvokeDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportLineageEdgeToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineageedge/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineageedge/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportLineageEdgeToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineageedge/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineageedge/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnLineageEdgeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageEdge> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.LineageEdge>> GetLineageEdge(Query query = null)
        {
            var items = Context.LineageEdge.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnLineageEdgeRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnLineageEdgeGet(SQLFlowUi.Models.sqlflowProd.LineageEdge item);
        partial void OnGetLineageEdgeByRecId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageEdge> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.LineageEdge> GetLineageEdgeByRecId(int recid)
        {
            var items = Context.LineageEdge
                              .AsNoTracking()
                              .Where(i => i.RecID == recid);

 
            OnGetLineageEdgeByRecId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnLineageEdgeGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnLineageEdgeCreated(SQLFlowUi.Models.sqlflowProd.LineageEdge item);
        partial void OnAfterLineageEdgeCreated(SQLFlowUi.Models.sqlflowProd.LineageEdge item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageEdge> CreateLineageEdge(SQLFlowUi.Models.sqlflowProd.LineageEdge lineageedge)
        {
            OnLineageEdgeCreated(lineageedge);

            var existingItem = Context.LineageEdge
                              .Where(i => i.RecID == lineageedge.RecID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.LineageEdge.Add(lineageedge);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(lineageedge).State = EntityState.Detached;
                throw;
            }

            OnAfterLineageEdgeCreated(lineageedge);

            return lineageedge;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageEdge> CancelLineageEdgeChanges(SQLFlowUi.Models.sqlflowProd.LineageEdge item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnLineageEdgeUpdated(SQLFlowUi.Models.sqlflowProd.LineageEdge item);
        partial void OnAfterLineageEdgeUpdated(SQLFlowUi.Models.sqlflowProd.LineageEdge item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageEdge> UpdateLineageEdge(int recid, SQLFlowUi.Models.sqlflowProd.LineageEdge lineageedge)
        {
            OnLineageEdgeUpdated(lineageedge);

            var itemToUpdate = Context.LineageEdge
                              .Where(i => i.RecID == lineageedge.RecID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(lineageedge);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterLineageEdgeUpdated(lineageedge);

            return lineageedge;
        }

        partial void OnLineageEdgeDeleted(SQLFlowUi.Models.sqlflowProd.LineageEdge item);
        partial void OnAfterLineageEdgeDeleted(SQLFlowUi.Models.sqlflowProd.LineageEdge item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageEdge> DeleteLineageEdge(int recid)
        {
            var itemToDelete = Context.LineageEdge
                              .Where(i => i.RecID == recid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnLineageEdgeDeleted(itemToDelete);


            Context.LineageEdge.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterLineageEdgeDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportLineageMapToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineagemap/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineagemap/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportLineageMapToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineagemap/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineagemap/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnLineageMapRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageMap> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.LineageMap>> GetLineageMap(Query query = null)
        {
            var items = Context.LineageMap.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnLineageMapRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnLineageMapGet(SQLFlowUi.Models.sqlflowProd.LineageMap item);
        partial void OnGetLineageMapByLineageParsedId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageMap> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.LineageMap> GetLineageMapByLineageParsedId(int lineageparsedid)
        {
            var items = Context.LineageMap
                              .AsNoTracking()
                              .Where(i => i.LineageParsedID == lineageparsedid);

 
            OnGetLineageMapByLineageParsedId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnLineageMapGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnLineageMapCreated(SQLFlowUi.Models.sqlflowProd.LineageMap item);
        partial void OnAfterLineageMapCreated(SQLFlowUi.Models.sqlflowProd.LineageMap item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageMap> CreateLineageMap(SQLFlowUi.Models.sqlflowProd.LineageMap lineagemap)
        {
            OnLineageMapCreated(lineagemap);

            var existingItem = Context.LineageMap
                              .Where(i => i.LineageParsedID == lineagemap.LineageParsedID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.LineageMap.Add(lineagemap);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(lineagemap).State = EntityState.Detached;
                throw;
            }

            OnAfterLineageMapCreated(lineagemap);

            return lineagemap;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageMap> CancelLineageMapChanges(SQLFlowUi.Models.sqlflowProd.LineageMap item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnLineageMapUpdated(SQLFlowUi.Models.sqlflowProd.LineageMap item);
        partial void OnAfterLineageMapUpdated(SQLFlowUi.Models.sqlflowProd.LineageMap item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageMap> UpdateLineageMap(int lineageparsedid, SQLFlowUi.Models.sqlflowProd.LineageMap lineagemap)
        {
            OnLineageMapUpdated(lineagemap);

            var itemToUpdate = Context.LineageMap
                              .Where(i => i.LineageParsedID == lineagemap.LineageParsedID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(lineagemap);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterLineageMapUpdated(lineagemap);

            return lineagemap;
        }

        partial void OnLineageMapDeleted(SQLFlowUi.Models.sqlflowProd.LineageMap item);
        partial void OnAfterLineageMapDeleted(SQLFlowUi.Models.sqlflowProd.LineageMap item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageMap> DeleteLineageMap(int lineageparsedid)
        {
            var itemToDelete = Context.LineageMap
                              .Where(i => i.LineageParsedID == lineageparsedid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnLineageMapDeleted(itemToDelete);


            Context.LineageMap.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterLineageMapDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportLineageObjectMKToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineageobjectmk/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineageobjectmk/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportLineageObjectMKToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineageobjectmk/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineageobjectmk/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnLineageObjectMKRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.LineageObjectMK>> GetLineageObjectMK(Query query = null)
        {
            var items = Context.LineageObjectMK.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnLineageObjectMKRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnLineageObjectMKGet(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item);
        partial void OnGetLineageObjectMKByObjectMk(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> GetLineageObjectMKByObjectMk(int objectmk)
        {
            var items = Context.LineageObjectMK
                              .AsNoTracking()
                              .Where(i => i.ObjectMK == objectmk);

 
            OnGetLineageObjectMKByObjectMk(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnLineageObjectMKGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnLineageObjectMKCreated(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item);
        partial void OnAfterLineageObjectMKCreated(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> CreateLineageObjectMK(SQLFlowUi.Models.sqlflowProd.LineageObjectMK lineageobjectmk)
        {
            OnLineageObjectMKCreated(lineageobjectmk);

            var existingItem = Context.LineageObjectMK
                              .Where(i => i.ObjectMK == lineageobjectmk.ObjectMK)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.LineageObjectMK.Add(lineageobjectmk);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(lineageobjectmk).State = EntityState.Detached;
                throw;
            }

            OnAfterLineageObjectMKCreated(lineageobjectmk);

            return lineageobjectmk;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> CancelLineageObjectMKChanges(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnLineageObjectMKUpdated(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item);
        partial void OnAfterLineageObjectMKUpdated(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> UpdateLineageObjectMK(int objectmk, SQLFlowUi.Models.sqlflowProd.LineageObjectMK lineageobjectmk)
        {
            OnLineageObjectMKUpdated(lineageobjectmk);

            var itemToUpdate = Context.LineageObjectMK
                              .Where(i => i.ObjectMK == lineageobjectmk.ObjectMK)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(lineageobjectmk);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterLineageObjectMKUpdated(lineageobjectmk);

            return lineageobjectmk;
        }

        partial void OnLineageObjectMKDeleted(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item);
        partial void OnAfterLineageObjectMKDeleted(SQLFlowUi.Models.sqlflowProd.LineageObjectMK item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> DeleteLineageObjectMK(int objectmk)
        {
            var itemToDelete = Context.LineageObjectMK
                              .Where(i => i.ObjectMK == objectmk)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnLineageObjectMKDeleted(itemToDelete);


            Context.LineageObjectMK.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterLineageObjectMKDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportLineageObjectRelationToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineageobjectrelation/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineageobjectrelation/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportLineageObjectRelationToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/lineageobjectrelation/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/lineageobjectrelation/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnLineageObjectRelationRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation>> GetLineageObjectRelation(Query query = null)
        {
            var items = Context.LineageObjectRelation.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnLineageObjectRelationRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnLineageObjectRelationGet(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item);
        partial void OnGetLineageObjectRelationByObjectRelationId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> GetLineageObjectRelationByObjectRelationId(int objectrelationid)
        {
            var items = Context.LineageObjectRelation
                              .AsNoTracking()
                              .Where(i => i.ObjectRelationID == objectrelationid);

 
            OnGetLineageObjectRelationByObjectRelationId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnLineageObjectRelationGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnLineageObjectRelationCreated(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item);
        partial void OnAfterLineageObjectRelationCreated(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> CreateLineageObjectRelation(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation lineageobjectrelation)
        {
            OnLineageObjectRelationCreated(lineageobjectrelation);

            var existingItem = Context.LineageObjectRelation
                              .Where(i => i.ObjectRelationID == lineageobjectrelation.ObjectRelationID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.LineageObjectRelation.Add(lineageobjectrelation);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(lineageobjectrelation).State = EntityState.Detached;
                throw;
            }

            OnAfterLineageObjectRelationCreated(lineageobjectrelation);

            return lineageobjectrelation;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> CancelLineageObjectRelationChanges(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnLineageObjectRelationUpdated(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item);
        partial void OnAfterLineageObjectRelationUpdated(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> UpdateLineageObjectRelation(int objectrelationid, SQLFlowUi.Models.sqlflowProd.LineageObjectRelation lineageobjectrelation)
        {
            OnLineageObjectRelationUpdated(lineageobjectrelation);

            var itemToUpdate = Context.LineageObjectRelation
                              .Where(i => i.ObjectRelationID == lineageobjectrelation.ObjectRelationID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(lineageobjectrelation);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterLineageObjectRelationUpdated(lineageobjectrelation);

            return lineageobjectrelation;
        }

        partial void OnLineageObjectRelationDeleted(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item);
        partial void OnAfterLineageObjectRelationDeleted(SQLFlowUi.Models.sqlflowProd.LineageObjectRelation item);

        public async Task<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> DeleteLineageObjectRelation(int objectrelationid)
        {
            var itemToDelete = Context.LineageObjectRelation
                              .Where(i => i.ObjectRelationID == objectrelationid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnLineageObjectRelationDeleted(itemToDelete);


            Context.LineageObjectRelation.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterLineageObjectRelationDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportMatchKeyToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/matchkey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/matchkey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportMatchKeyToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/matchkey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/matchkey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnMatchKeyRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.MatchKey> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.MatchKey>> GetMatchKey(Query query = null)
        {
            var items = Context.MatchKey.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnMatchKeyRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnMatchKeyGet(SQLFlowUi.Models.sqlflowProd.MatchKey item);
        partial void OnGetMatchKeyByMatchKeyId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.MatchKey> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.MatchKey> GetMatchKeyByMatchKeyId(int matchkeyid)
        {
            var items = Context.MatchKey
                              .AsNoTracking()
                              .Where(i => i.MatchKeyID == matchkeyid);

 
            OnGetMatchKeyByMatchKeyId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnMatchKeyGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnMatchKeyCreated(SQLFlowUi.Models.sqlflowProd.MatchKey item);
        partial void OnAfterMatchKeyCreated(SQLFlowUi.Models.sqlflowProd.MatchKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.MatchKey> CreateMatchKey(SQLFlowUi.Models.sqlflowProd.MatchKey matchkey)
        {
            OnMatchKeyCreated(matchkey);

            var existingItem = Context.MatchKey
                              .Where(i => i.MatchKeyID == matchkey.MatchKeyID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.MatchKey.Add(matchkey);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(matchkey).State = EntityState.Detached;
                throw;
            }

            OnAfterMatchKeyCreated(matchkey);

            return matchkey;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.MatchKey> CancelMatchKeyChanges(SQLFlowUi.Models.sqlflowProd.MatchKey item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnMatchKeyUpdated(SQLFlowUi.Models.sqlflowProd.MatchKey item);
        partial void OnAfterMatchKeyUpdated(SQLFlowUi.Models.sqlflowProd.MatchKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.MatchKey> UpdateMatchKey(int matchkeyid, SQLFlowUi.Models.sqlflowProd.MatchKey matchkey)
        {
            OnMatchKeyUpdated(matchkey);

            var itemToUpdate = Context.MatchKey
                              .Where(i => i.MatchKeyID == matchkey.MatchKeyID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(matchkey);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterMatchKeyUpdated(matchkey);

            return matchkey;
        }

        partial void OnMatchKeyDeleted(SQLFlowUi.Models.sqlflowProd.MatchKey item);
        partial void OnAfterMatchKeyDeleted(SQLFlowUi.Models.sqlflowProd.MatchKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.MatchKey> DeleteMatchKey(int matchkeyid)
        {
            var itemToDelete = Context.MatchKey
                              .Where(i => i.MatchKeyID == matchkeyid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnMatchKeyDeleted(itemToDelete);


            Context.MatchKey.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterMatchKeyDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportParameterToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/parameter/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/parameter/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportParameterToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/parameter/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/parameter/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnParameterRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Parameter> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.Parameter>> GetParameter(Query query = null)
        {
            var items = Context.Parameter.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnParameterRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnParameterGet(SQLFlowUi.Models.sqlflowProd.Parameter item);
        partial void OnGetParameterByParameterId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.Parameter> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.Parameter> GetParameterByParameterId(int parameterid)
        {
            var items = Context.Parameter
                              .AsNoTracking()
                              .Where(i => i.ParameterID == parameterid);

 
            OnGetParameterByParameterId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnParameterGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnParameterCreated(SQLFlowUi.Models.sqlflowProd.Parameter item);
        partial void OnAfterParameterCreated(SQLFlowUi.Models.sqlflowProd.Parameter item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Parameter> CreateParameter(SQLFlowUi.Models.sqlflowProd.Parameter parameter)
        {
            OnParameterCreated(parameter);

            var existingItem = Context.Parameter
                              .Where(i => i.ParameterID == parameter.ParameterID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.Parameter.Add(parameter);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(parameter).State = EntityState.Detached;
                throw;
            }

            OnAfterParameterCreated(parameter);

            return parameter;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.Parameter> CancelParameterChanges(SQLFlowUi.Models.sqlflowProd.Parameter item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnParameterUpdated(SQLFlowUi.Models.sqlflowProd.Parameter item);
        partial void OnAfterParameterUpdated(SQLFlowUi.Models.sqlflowProd.Parameter item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Parameter> UpdateParameter(int parameterid, SQLFlowUi.Models.sqlflowProd.Parameter parameter)
        {
            OnParameterUpdated(parameter);

            var itemToUpdate = Context.Parameter
                              .Where(i => i.ParameterID == parameter.ParameterID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(parameter);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterParameterUpdated(parameter);

            return parameter;
        }

        partial void OnParameterDeleted(SQLFlowUi.Models.sqlflowProd.Parameter item);
        partial void OnAfterParameterDeleted(SQLFlowUi.Models.sqlflowProd.Parameter item);

        public async Task<SQLFlowUi.Models.sqlflowProd.Parameter> DeleteParameter(int parameterid)
        {
            var itemToDelete = Context.Parameter
                              .Where(i => i.ParameterID == parameterid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnParameterDeleted(itemToDelete);


            Context.Parameter.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterParameterDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionADOToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionado/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionado/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionADOToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionado/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionado/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionADORead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>> GetPreIngestionADO(Query query = null)
        {
            var items = Context.PreIngestionADO.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionADORead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionADOGet(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item);
        partial void OnGetPreIngestionADOByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> GetPreIngestionADOByFlowId(int flowid)
        {
            var items = Context.PreIngestionADO
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetPreIngestionADOByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionADOGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionADOCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item);
        partial void OnAfterPreIngestionADOCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> CreatePreIngestionADO(SQLFlowUi.Models.sqlflowProd.PreIngestionADO preingestionado)
        {
            OnPreIngestionADOCreated(preingestionado);

            var existingItem = Context.PreIngestionADO
                              .Where(i => i.FlowID == preingestionado.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionADO.Add(preingestionado);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestionado).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionADOCreated(preingestionado);

            return preingestionado;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> CancelPreIngestionADOChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionADOUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item);
        partial void OnAfterPreIngestionADOUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> UpdatePreIngestionADO(int flowid, SQLFlowUi.Models.sqlflowProd.PreIngestionADO preingestionado)
        {
            OnPreIngestionADOUpdated(preingestionado);

            var itemToUpdate = Context.PreIngestionADO
                              .Where(i => i.FlowID == preingestionado.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestionado);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionADOUpdated(preingestionado);

            return preingestionado;
        }

        partial void OnPreIngestionADODeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item);
        partial void OnAfterPreIngestionADODeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionADO item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> DeletePreIngestionADO(int flowid)
        {
            var itemToDelete = Context.PreIngestionADO
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionADODeleted(itemToDelete);


            Context.PreIngestionADO.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionADODeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionADOVirtualToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionadovirtual/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionadovirtual/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionADOVirtualToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionadovirtual/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionadovirtual/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionADOVirtualRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual>> GetPreIngestionADOVirtual(Query query = null)
        {
            var items = Context.PreIngestionADOVirtual.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionADOVirtualRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionADOVirtualGet(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item);
        partial void OnGetPreIngestionADOVirtualByVirtualId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> GetPreIngestionADOVirtualByVirtualId(int virtualid)
        {
            var items = Context.PreIngestionADOVirtual
                              .AsNoTracking()
                              .Where(i => i.VirtualID == virtualid);

 
            OnGetPreIngestionADOVirtualByVirtualId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionADOVirtualGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionADOVirtualCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item);
        partial void OnAfterPreIngestionADOVirtualCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> CreatePreIngestionADOVirtual(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual preingestionadovirtual)
        {
            OnPreIngestionADOVirtualCreated(preingestionadovirtual);

            var existingItem = Context.PreIngestionADOVirtual
                              .Where(i => i.VirtualID == preingestionadovirtual.VirtualID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionADOVirtual.Add(preingestionadovirtual);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestionadovirtual).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionADOVirtualCreated(preingestionadovirtual);

            return preingestionadovirtual;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> CancelPreIngestionADOVirtualChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionADOVirtualUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item);
        partial void OnAfterPreIngestionADOVirtualUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> UpdatePreIngestionADOVirtual(int virtualid, SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual preingestionadovirtual)
        {
            OnPreIngestionADOVirtualUpdated(preingestionadovirtual);

            var itemToUpdate = Context.PreIngestionADOVirtual
                              .Where(i => i.VirtualID == preingestionadovirtual.VirtualID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestionadovirtual);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionADOVirtualUpdated(preingestionadovirtual);

            return preingestionadovirtual;
        }

        partial void OnPreIngestionADOVirtualDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item);
        partial void OnAfterPreIngestionADOVirtualDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> DeletePreIngestionADOVirtual(int virtualid)
        {
            var itemToDelete = Context.PreIngestionADOVirtual
                              .Where(i => i.VirtualID == virtualid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionADOVirtualDeleted(itemToDelete);


            Context.PreIngestionADOVirtual.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionADOVirtualDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionCSVToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestioncsv/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestioncsv/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionCSVToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestioncsv/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestioncsv/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionCSVRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>> GetPreIngestionCSV(Query query = null)
        {
            var items = Context.PreIngestionCSV.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionCSVRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionCSVGet(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item);
        partial void OnGetPreIngestionCSVByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> GetPreIngestionCSVByFlowId(int flowid)
        {
            var items = Context.PreIngestionCSV
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetPreIngestionCSVByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionCSVGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionCSVCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item);
        partial void OnAfterPreIngestionCSVCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> CreatePreIngestionCSV(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV preingestioncsv)
        {
            OnPreIngestionCSVCreated(preingestioncsv);

            var existingItem = Context.PreIngestionCSV
                              .Where(i => i.FlowID == preingestioncsv.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionCSV.Add(preingestioncsv);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestioncsv).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionCSVCreated(preingestioncsv);

            return preingestioncsv;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> CancelPreIngestionCSVChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionCSVUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item);
        partial void OnAfterPreIngestionCSVUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> UpdatePreIngestionCSV(int flowid, SQLFlowUi.Models.sqlflowProd.PreIngestionCSV preingestioncsv)
        {
            OnPreIngestionCSVUpdated(preingestioncsv);

            var itemToUpdate = Context.PreIngestionCSV
                              .Where(i => i.FlowID == preingestioncsv.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestioncsv);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionCSVUpdated(preingestioncsv);

            return preingestioncsv;
        }

        partial void OnPreIngestionCSVDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item);
        partial void OnAfterPreIngestionCSVDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionCSV item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> DeletePreIngestionCSV(int flowid)
        {
            var itemToDelete = Context.PreIngestionCSV
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionCSVDeleted(itemToDelete);


            Context.PreIngestionCSV.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionCSVDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionJSNToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionjsn/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionjsn/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionJSNToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionjsn/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionjsn/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionJSNRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>> GetPreIngestionJSN(Query query = null)
        {
            var items = Context.PreIngestionJSN.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionJSNRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionJSNGet(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item);
        partial void OnGetPreIngestionJSNByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> GetPreIngestionJSNByFlowId(int flowid)
        {
            var items = Context.PreIngestionJSN
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetPreIngestionJSNByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionJSNGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionJSNCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item);
        partial void OnAfterPreIngestionJSNCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> CreatePreIngestionJSN(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN preingestionjsn)
        {
            OnPreIngestionJSNCreated(preingestionjsn);

            var existingItem = Context.PreIngestionJSN
                              .Where(i => i.FlowID == preingestionjsn.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionJSN.Add(preingestionjsn);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestionjsn).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionJSNCreated(preingestionjsn);

            return preingestionjsn;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> CancelPreIngestionJSNChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionJSNUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item);
        partial void OnAfterPreIngestionJSNUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> UpdatePreIngestionJSN(int flowid, SQLFlowUi.Models.sqlflowProd.PreIngestionJSN preingestionjsn)
        {
            OnPreIngestionJSNUpdated(preingestionjsn);

            var itemToUpdate = Context.PreIngestionJSN
                              .Where(i => i.FlowID == preingestionjsn.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestionjsn);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionJSNUpdated(preingestionjsn);

            return preingestionjsn;
        }

        partial void OnPreIngestionJSNDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item);
        partial void OnAfterPreIngestionJSNDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> DeletePreIngestionJSN(int flowid)
        {
            var itemToDelete = Context.PreIngestionJSN
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionJSNDeleted(itemToDelete);


            Context.PreIngestionJSN.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionJSNDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionPRCToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionprc/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionprc/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionPRCToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionprc/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionprc/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionPRCRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>> GetPreIngestionPRC(Query query = null)
        {
            var items = Context.PreIngestionPRC.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionPRCRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionPRCGet(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item);
        partial void OnGetPreIngestionPRCByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> GetPreIngestionPRCByFlowId(int flowid)
        {
            var items = Context.PreIngestionPRC
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetPreIngestionPRCByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionPRCGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionPRCCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item);
        partial void OnAfterPreIngestionPRCCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> CreatePreIngestionPRC(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC preingestionprc)
        {
            OnPreIngestionPRCCreated(preingestionprc);

            var existingItem = Context.PreIngestionPRC
                              .Where(i => i.FlowID == preingestionprc.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionPRC.Add(preingestionprc);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestionprc).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionPRCCreated(preingestionprc);

            return preingestionprc;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> CancelPreIngestionPRCChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionPRCUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item);
        partial void OnAfterPreIngestionPRCUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> UpdatePreIngestionPRC(int flowid, SQLFlowUi.Models.sqlflowProd.PreIngestionPRC preingestionprc)
        {
            OnPreIngestionPRCUpdated(preingestionprc);

            var itemToUpdate = Context.PreIngestionPRC
                              .Where(i => i.FlowID == preingestionprc.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestionprc);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionPRCUpdated(preingestionprc);

            return preingestionprc;
        }

        partial void OnPreIngestionPRCDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item);
        partial void OnAfterPreIngestionPRCDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionPRC item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> DeletePreIngestionPRC(int flowid)
        {
            var itemToDelete = Context.PreIngestionPRC
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionPRCDeleted(itemToDelete);


            Context.PreIngestionPRC.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionPRCDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionPRQToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionprq/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionprq/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionPRQToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionprq/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionprq/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionPRQRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>> GetPreIngestionPRQ(Query query = null)
        {
            var items = Context.PreIngestionPRQ.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionPRQRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionPRQGet(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item);
        partial void OnGetPreIngestionPRQByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> GetPreIngestionPRQByFlowId(int flowid)
        {
            var items = Context.PreIngestionPRQ
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetPreIngestionPRQByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionPRQGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionPRQCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item);
        partial void OnAfterPreIngestionPRQCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> CreatePreIngestionPRQ(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ preingestionprq)
        {
            OnPreIngestionPRQCreated(preingestionprq);

            var existingItem = Context.PreIngestionPRQ
                              .Where(i => i.FlowID == preingestionprq.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionPRQ.Add(preingestionprq);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestionprq).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionPRQCreated(preingestionprq);

            return preingestionprq;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> CancelPreIngestionPRQChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionPRQUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item);
        partial void OnAfterPreIngestionPRQUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> UpdatePreIngestionPRQ(int flowid, SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ preingestionprq)
        {
            OnPreIngestionPRQUpdated(preingestionprq);

            var itemToUpdate = Context.PreIngestionPRQ
                              .Where(i => i.FlowID == preingestionprq.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestionprq);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionPRQUpdated(preingestionprq);

            return preingestionprq;
        }

        partial void OnPreIngestionPRQDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item);
        partial void OnAfterPreIngestionPRQDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> DeletePreIngestionPRQ(int flowid)
        {
            var itemToDelete = Context.PreIngestionPRQ
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionPRQDeleted(itemToDelete);


            Context.PreIngestionPRQ.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionPRQDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionTransfromToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestiontransfrom/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestiontransfrom/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionTransfromToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestiontransfrom/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestiontransfrom/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionTransfromRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom>> GetPreIngestionTransfrom(Query query = null)
        {
            var items = Context.PreIngestionTransfrom.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionTransfromRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionTransfromGet(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item);
        partial void OnGetPreIngestionTransfromByTransfromId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> GetPreIngestionTransfromByTransfromId(int transfromid)
        {
            var items = Context.PreIngestionTransfrom
                              .AsNoTracking()
                              .Where(i => i.TransfromID == transfromid);

 
            OnGetPreIngestionTransfromByTransfromId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionTransfromGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionTransfromCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item);
        partial void OnAfterPreIngestionTransfromCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> CreatePreIngestionTransfrom(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom preingestiontransfrom)
        {
            OnPreIngestionTransfromCreated(preingestiontransfrom);

            var existingItem = Context.PreIngestionTransfrom
                              .Where(i => i.TransfromID == preingestiontransfrom.TransfromID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionTransfrom.Add(preingestiontransfrom);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestiontransfrom).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionTransfromCreated(preingestiontransfrom);

            return preingestiontransfrom;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> CancelPreIngestionTransfromChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionTransfromUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item);
        partial void OnAfterPreIngestionTransfromUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> UpdatePreIngestionTransfrom(int transfromid, SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom preingestiontransfrom)
        {
            OnPreIngestionTransfromUpdated(preingestiontransfrom);

            var itemToUpdate = Context.PreIngestionTransfrom
                              .Where(i => i.TransfromID == preingestiontransfrom.TransfromID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestiontransfrom);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionTransfromUpdated(preingestiontransfrom);

            return preingestiontransfrom;
        }

        partial void OnPreIngestionTransfromDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item);
        partial void OnAfterPreIngestionTransfromDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> DeletePreIngestionTransfrom(int transfromid)
        {
            var itemToDelete = Context.PreIngestionTransfrom
                              .Where(i => i.TransfromID == transfromid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionTransfromDeleted(itemToDelete);


            Context.PreIngestionTransfrom.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionTransfromDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionXLSToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionxls/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionxls/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionXLSToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionxls/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionxls/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionXLSRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>> GetPreIngestionXLS(Query query = null)
        {
            var items = Context.PreIngestionXLS.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionXLSRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionXLSGet(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item);
        partial void OnGetPreIngestionXLSByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> GetPreIngestionXLSByFlowId(int flowid)
        {
            var items = Context.PreIngestionXLS
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetPreIngestionXLSByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionXLSGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionXLSCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item);
        partial void OnAfterPreIngestionXLSCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> CreatePreIngestionXLS(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS preingestionxls)
        {
            OnPreIngestionXLSCreated(preingestionxls);

            var existingItem = Context.PreIngestionXLS
                              .Where(i => i.FlowID == preingestionxls.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionXLS.Add(preingestionxls);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestionxls).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionXLSCreated(preingestionxls);

            return preingestionxls;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> CancelPreIngestionXLSChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionXLSUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item);
        partial void OnAfterPreIngestionXLSUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> UpdatePreIngestionXLS(int flowid, SQLFlowUi.Models.sqlflowProd.PreIngestionXLS preingestionxls)
        {
            OnPreIngestionXLSUpdated(preingestionxls);

            var itemToUpdate = Context.PreIngestionXLS
                              .Where(i => i.FlowID == preingestionxls.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestionxls);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionXLSUpdated(preingestionxls);

            return preingestionxls;
        }

        partial void OnPreIngestionXLSDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item);
        partial void OnAfterPreIngestionXLSDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionXLS item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> DeletePreIngestionXLS(int flowid)
        {
            var itemToDelete = Context.PreIngestionXLS
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionXLSDeleted(itemToDelete);


            Context.PreIngestionXLS.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionXLSDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportPreIngestionXMLToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionxml/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionxml/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportPreIngestionXMLToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/preingestionxml/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/preingestionxml/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnPreIngestionXMLRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>> GetPreIngestionXML(Query query = null)
        {
            var items = Context.PreIngestionXML.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnPreIngestionXMLRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnPreIngestionXMLGet(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item);
        partial void OnGetPreIngestionXMLByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> GetPreIngestionXMLByFlowId(int flowid)
        {
            var items = Context.PreIngestionXML
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetPreIngestionXMLByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnPreIngestionXMLGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnPreIngestionXMLCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item);
        partial void OnAfterPreIngestionXMLCreated(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> CreatePreIngestionXML(SQLFlowUi.Models.sqlflowProd.PreIngestionXML preingestionxml)
        {
            OnPreIngestionXMLCreated(preingestionxml);

            var existingItem = Context.PreIngestionXML
                              .Where(i => i.FlowID == preingestionxml.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.PreIngestionXML.Add(preingestionxml);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(preingestionxml).State = EntityState.Detached;
                throw;
            }

            OnAfterPreIngestionXMLCreated(preingestionxml);

            return preingestionxml;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> CancelPreIngestionXMLChanges(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnPreIngestionXMLUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item);
        partial void OnAfterPreIngestionXMLUpdated(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> UpdatePreIngestionXML(int flowid, SQLFlowUi.Models.sqlflowProd.PreIngestionXML preingestionxml)
        {
            OnPreIngestionXMLUpdated(preingestionxml);

            var itemToUpdate = Context.PreIngestionXML
                              .Where(i => i.FlowID == preingestionxml.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(preingestionxml);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterPreIngestionXMLUpdated(preingestionxml);

            return preingestionxml;
        }

        partial void OnPreIngestionXMLDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item);
        partial void OnAfterPreIngestionXMLDeleted(SQLFlowUi.Models.sqlflowProd.PreIngestionXML item);

        public async Task<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> DeletePreIngestionXML(int flowid)
        {
            var itemToDelete = Context.PreIngestionXML
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnPreIngestionXMLDeleted(itemToDelete);


            Context.PreIngestionXML.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterPreIngestionXMLDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportStoredProcedureToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/storedprocedure/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/storedprocedure/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportStoredProcedureToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/storedprocedure/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/storedprocedure/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnStoredProcedureRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.StoredProcedure> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.StoredProcedure>> GetStoredProcedure(Query query = null)
        {
            var items = Context.StoredProcedure.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnStoredProcedureRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnStoredProcedureGet(SQLFlowUi.Models.sqlflowProd.StoredProcedure item);
        partial void OnGetStoredProcedureByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.StoredProcedure> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.StoredProcedure> GetStoredProcedureByFlowId(int flowid)
        {
            var items = Context.StoredProcedure
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetStoredProcedureByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnStoredProcedureGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnStoredProcedureCreated(SQLFlowUi.Models.sqlflowProd.StoredProcedure item);
        partial void OnAfterStoredProcedureCreated(SQLFlowUi.Models.sqlflowProd.StoredProcedure item);

        public async Task<SQLFlowUi.Models.sqlflowProd.StoredProcedure> CreateStoredProcedure(SQLFlowUi.Models.sqlflowProd.StoredProcedure storedprocedure)
        {
            OnStoredProcedureCreated(storedprocedure);

            var existingItem = Context.StoredProcedure
                              .Where(i => i.FlowID == storedprocedure.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.StoredProcedure.Add(storedprocedure);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(storedprocedure).State = EntityState.Detached;
                throw;
            }

            OnAfterStoredProcedureCreated(storedprocedure);

            return storedprocedure;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.StoredProcedure> CancelStoredProcedureChanges(SQLFlowUi.Models.sqlflowProd.StoredProcedure item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnStoredProcedureUpdated(SQLFlowUi.Models.sqlflowProd.StoredProcedure item);
        partial void OnAfterStoredProcedureUpdated(SQLFlowUi.Models.sqlflowProd.StoredProcedure item);

        public async Task<SQLFlowUi.Models.sqlflowProd.StoredProcedure> UpdateStoredProcedure(int flowid, SQLFlowUi.Models.sqlflowProd.StoredProcedure storedprocedure)
        {
            OnStoredProcedureUpdated(storedprocedure);

            var itemToUpdate = Context.StoredProcedure
                              .Where(i => i.FlowID == storedprocedure.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(storedprocedure);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterStoredProcedureUpdated(storedprocedure);

            return storedprocedure;
        }

        partial void OnStoredProcedureDeleted(SQLFlowUi.Models.sqlflowProd.StoredProcedure item);
        partial void OnAfterStoredProcedureDeleted(SQLFlowUi.Models.sqlflowProd.StoredProcedure item);

        public async Task<SQLFlowUi.Models.sqlflowProd.StoredProcedure> DeleteStoredProcedure(int flowid)
        {
            var itemToDelete = Context.StoredProcedure
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnStoredProcedureDeleted(itemToDelete);


            Context.StoredProcedure.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterStoredProcedureDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSurrogateKeyToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/surrogatekey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/surrogatekey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSurrogateKeyToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/surrogatekey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/surrogatekey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSurrogateKeyRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SurrogateKey> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SurrogateKey>> GetSurrogateKey(Query query = null)
        {
            var items = Context.SurrogateKey.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSurrogateKeyRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSurrogateKeyGet(SQLFlowUi.Models.sqlflowProd.SurrogateKey item);
        partial void OnGetSurrogateKeyBySurrogateKeyId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SurrogateKey> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SurrogateKey> GetSurrogateKeyBySurrogateKeyId(int surrogatekeyid)
        {
            var items = Context.SurrogateKey
                              .AsNoTracking()
                              .Where(i => i.SurrogateKeyID == surrogatekeyid);

 
            OnGetSurrogateKeyBySurrogateKeyId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSurrogateKeyGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSurrogateKeyCreated(SQLFlowUi.Models.sqlflowProd.SurrogateKey item);
        partial void OnAfterSurrogateKeyCreated(SQLFlowUi.Models.sqlflowProd.SurrogateKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SurrogateKey> CreateSurrogateKey(SQLFlowUi.Models.sqlflowProd.SurrogateKey surrogatekey)
        {
            OnSurrogateKeyCreated(surrogatekey);

            var existingItem = Context.SurrogateKey
                              .Where(i => i.SurrogateKeyID == surrogatekey.SurrogateKeyID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SurrogateKey.Add(surrogatekey);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(surrogatekey).State = EntityState.Detached;
                throw;
            }

            OnAfterSurrogateKeyCreated(surrogatekey);

            return surrogatekey;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SurrogateKey> CancelSurrogateKeyChanges(SQLFlowUi.Models.sqlflowProd.SurrogateKey item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSurrogateKeyUpdated(SQLFlowUi.Models.sqlflowProd.SurrogateKey item);
        partial void OnAfterSurrogateKeyUpdated(SQLFlowUi.Models.sqlflowProd.SurrogateKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SurrogateKey> UpdateSurrogateKey(int surrogatekeyid, SQLFlowUi.Models.sqlflowProd.SurrogateKey surrogatekey)
        {
            OnSurrogateKeyUpdated(surrogatekey);

            var itemToUpdate = Context.SurrogateKey
                              .Where(i => i.SurrogateKeyID == surrogatekey.SurrogateKeyID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(surrogatekey);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSurrogateKeyUpdated(surrogatekey);

            return surrogatekey;
        }

        partial void OnSurrogateKeyDeleted(SQLFlowUi.Models.sqlflowProd.SurrogateKey item);
        partial void OnAfterSurrogateKeyDeleted(SQLFlowUi.Models.sqlflowProd.SurrogateKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SurrogateKey> DeleteSurrogateKey(int surrogatekeyid)
        {
            var itemToDelete = Context.SurrogateKey
                              .Where(i => i.SurrogateKeyID == surrogatekeyid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSurrogateKeyDeleted(itemToDelete);


            Context.SurrogateKey.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSurrogateKeyDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysAIPromptToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysaiprompt/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysaiprompt/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysAIPromptToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysaiprompt/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysaiprompt/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysAIPromptRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysAIPrompt>> GetSysAIPrompt(Query query = null)
        {
            var items = Context.SysAIPrompt.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysAIPromptRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysAIPromptGet(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item);
        partial void OnGetSysAIPromptByPromptId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> GetSysAIPromptByPromptId(int promptid)
        {
            var items = Context.SysAIPrompt
                              .AsNoTracking()
                              .Where(i => i.PromptID == promptid);

 
            OnGetSysAIPromptByPromptId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysAIPromptGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysAIPromptCreated(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item);
        partial void OnAfterSysAIPromptCreated(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> CreateSysAIPrompt(SQLFlowUi.Models.sqlflowProd.SysAIPrompt sysaiprompt)
        {
            OnSysAIPromptCreated(sysaiprompt);

            var existingItem = Context.SysAIPrompt
                              .Where(i => i.PromptID == sysaiprompt.PromptID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysAIPrompt.Add(sysaiprompt);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysaiprompt).State = EntityState.Detached;
                throw;
            }

            OnAfterSysAIPromptCreated(sysaiprompt);

            return sysaiprompt;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> CancelSysAIPromptChanges(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysAIPromptUpdated(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item);
        partial void OnAfterSysAIPromptUpdated(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> UpdateSysAIPrompt(int promptid, SQLFlowUi.Models.sqlflowProd.SysAIPrompt sysaiprompt)
        {
            OnSysAIPromptUpdated(sysaiprompt);

            var itemToUpdate = Context.SysAIPrompt
                              .Where(i => i.PromptID == sysaiprompt.PromptID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysaiprompt);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysAIPromptUpdated(sysaiprompt);

            return sysaiprompt;
        }

        partial void OnSysAIPromptDeleted(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item);
        partial void OnAfterSysAIPromptDeleted(SQLFlowUi.Models.sqlflowProd.SysAIPrompt item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> DeleteSysAIPrompt(int promptid)
        {
            var itemToDelete = Context.SysAIPrompt
                              .Where(i => i.PromptID == promptid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysAIPromptDeleted(itemToDelete);


            Context.SysAIPrompt.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysAIPromptDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysAliasToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysalias/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysalias/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysAliasToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysalias/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysalias/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysAliasRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysAlias> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysAlias>> GetSysAlias(Query query = null)
        {
            var items = Context.SysAlias.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysAliasRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysAliasGet(SQLFlowUi.Models.sqlflowProd.SysAlias item);
        partial void OnGetSysAliasBySystemId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysAlias> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysAlias> GetSysAliasBySystemId(int systemid)
        {
            var items = Context.SysAlias
                              .AsNoTracking()
                              .Where(i => i.SystemID == systemid);

 
            OnGetSysAliasBySystemId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysAliasGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysAliasCreated(SQLFlowUi.Models.sqlflowProd.SysAlias item);
        partial void OnAfterSysAliasCreated(SQLFlowUi.Models.sqlflowProd.SysAlias item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAlias> CreateSysAlias(SQLFlowUi.Models.sqlflowProd.SysAlias sysalias)
        {
            OnSysAliasCreated(sysalias);

            var existingItem = Context.SysAlias
                              .Where(i => i.SystemID == sysalias.SystemID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysAlias.Add(sysalias);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysalias).State = EntityState.Detached;
                throw;
            }

            OnAfterSysAliasCreated(sysalias);

            return sysalias;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAlias> CancelSysAliasChanges(SQLFlowUi.Models.sqlflowProd.SysAlias item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysAliasUpdated(SQLFlowUi.Models.sqlflowProd.SysAlias item);
        partial void OnAfterSysAliasUpdated(SQLFlowUi.Models.sqlflowProd.SysAlias item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAlias> UpdateSysAlias(int systemid, SQLFlowUi.Models.sqlflowProd.SysAlias sysalias)
        {
            OnSysAliasUpdated(sysalias);

            var itemToUpdate = Context.SysAlias
                              .Where(i => i.SystemID == sysalias.SystemID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysalias);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysAliasUpdated(sysalias);

            return sysalias;
        }

        partial void OnSysAliasDeleted(SQLFlowUi.Models.sqlflowProd.SysAlias item);
        partial void OnAfterSysAliasDeleted(SQLFlowUi.Models.sqlflowProd.SysAlias item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAlias> DeleteSysAlias(int systemid)
        {
            var itemToDelete = Context.SysAlias
                              .Where(i => i.SystemID == systemid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysAliasDeleted(itemToDelete);


            Context.SysAlias.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysAliasDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysAPIKeyToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysapikey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysapikey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysAPIKeyToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysapikey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysapikey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysAPIKeyRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysAPIKey> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysAPIKey>> GetSysAPIKey(Query query = null)
        {
            var items = Context.SysAPIKey.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysAPIKeyRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysAPIKeyGet(SQLFlowUi.Models.sqlflowProd.SysAPIKey item);
        partial void OnGetSysAPIKeyByApiKeyId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysAPIKey> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysAPIKey> GetSysAPIKeyByApiKeyId(int apikeyid)
        {
            var items = Context.SysAPIKey
                              .AsNoTracking()
                              .Where(i => i.ApiKeyID == apikeyid);

 
            OnGetSysAPIKeyByApiKeyId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysAPIKeyGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysAPIKeyCreated(SQLFlowUi.Models.sqlflowProd.SysAPIKey item);
        partial void OnAfterSysAPIKeyCreated(SQLFlowUi.Models.sqlflowProd.SysAPIKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAPIKey> CreateSysAPIKey(SQLFlowUi.Models.sqlflowProd.SysAPIKey sysapikey)
        {
            OnSysAPIKeyCreated(sysapikey);

            var existingItem = Context.SysAPIKey
                              .Where(i => i.ApiKeyID == sysapikey.ApiKeyID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysAPIKey.Add(sysapikey);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysapikey).State = EntityState.Detached;
                throw;
            }

            OnAfterSysAPIKeyCreated(sysapikey);

            return sysapikey;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAPIKey> CancelSysAPIKeyChanges(SQLFlowUi.Models.sqlflowProd.SysAPIKey item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysAPIKeyUpdated(SQLFlowUi.Models.sqlflowProd.SysAPIKey item);
        partial void OnAfterSysAPIKeyUpdated(SQLFlowUi.Models.sqlflowProd.SysAPIKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAPIKey> UpdateSysAPIKey(int apikeyid, SQLFlowUi.Models.sqlflowProd.SysAPIKey sysapikey)
        {
            OnSysAPIKeyUpdated(sysapikey);

            var itemToUpdate = Context.SysAPIKey
                              .Where(i => i.ApiKeyID == sysapikey.ApiKeyID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysapikey);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysAPIKeyUpdated(sysapikey);

            return sysapikey;
        }

        partial void OnSysAPIKeyDeleted(SQLFlowUi.Models.sqlflowProd.SysAPIKey item);
        partial void OnAfterSysAPIKeyDeleted(SQLFlowUi.Models.sqlflowProd.SysAPIKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysAPIKey> DeleteSysAPIKey(int apikeyid)
        {
            var itemToDelete = Context.SysAPIKey
                              .Where(i => i.ApiKeyID == apikeyid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysAPIKeyDeleted(itemToDelete);


            Context.SysAPIKey.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysAPIKeyDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysBatchToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysbatch/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysbatch/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysBatchToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysbatch/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysbatch/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysBatchRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysBatch> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysBatch>> GetSysBatch(Query query = null)
        {
            var items = Context.SysBatch.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysBatchRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysBatchGet(SQLFlowUi.Models.sqlflowProd.SysBatch item);
        partial void OnGetSysBatchBySysBatchId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysBatch> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysBatch> GetSysBatchBySysBatchId(int sysbatchid)
        {
            var items = Context.SysBatch
                              .AsNoTracking()
                              .Where(i => i.SysBatchID == sysbatchid);

 
            OnGetSysBatchBySysBatchId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysBatchGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysBatchCreated(SQLFlowUi.Models.sqlflowProd.SysBatch item);
        partial void OnAfterSysBatchCreated(SQLFlowUi.Models.sqlflowProd.SysBatch item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysBatch> CreateSysBatch(SQLFlowUi.Models.sqlflowProd.SysBatch sysbatch)
        {
            OnSysBatchCreated(sysbatch);

            var existingItem = Context.SysBatch
                              .Where(i => i.SysBatchID == sysbatch.SysBatchID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysBatch.Add(sysbatch);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysbatch).State = EntityState.Detached;
                throw;
            }

            OnAfterSysBatchCreated(sysbatch);

            return sysbatch;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysBatch> CancelSysBatchChanges(SQLFlowUi.Models.sqlflowProd.SysBatch item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysBatchUpdated(SQLFlowUi.Models.sqlflowProd.SysBatch item);
        partial void OnAfterSysBatchUpdated(SQLFlowUi.Models.sqlflowProd.SysBatch item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysBatch> UpdateSysBatch(int sysbatchid, SQLFlowUi.Models.sqlflowProd.SysBatch sysbatch)
        {
            OnSysBatchUpdated(sysbatch);

            var itemToUpdate = Context.SysBatch
                              .Where(i => i.SysBatchID == sysbatch.SysBatchID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysbatch);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysBatchUpdated(sysbatch);

            return sysbatch;
        }

        partial void OnSysBatchDeleted(SQLFlowUi.Models.sqlflowProd.SysBatch item);
        partial void OnAfterSysBatchDeleted(SQLFlowUi.Models.sqlflowProd.SysBatch item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysBatch> DeleteSysBatch(int sysbatchid)
        {
            var itemToDelete = Context.SysBatch
                              .Where(i => i.SysBatchID == sysbatchid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysBatchDeleted(itemToDelete);


            Context.SysBatch.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysBatchDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysCFGToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syscfg/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syscfg/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysCFGToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syscfg/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syscfg/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysCFGRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysCFG> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysCFG>> GetSysCFG(Query query = null)
        {
            var items = Context.SysCFG.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysCFGRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysCFGGet(SQLFlowUi.Models.sqlflowProd.SysCFG item);
        partial void OnGetSysCFGByParamName(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysCFG> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysCFG> GetSysCFGByParamName(string paramname)
        {
            var items = Context.SysCFG
                              .AsNoTracking()
                              .Where(i => i.ParamName == paramname);

 
            OnGetSysCFGByParamName(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysCFGGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysCFGCreated(SQLFlowUi.Models.sqlflowProd.SysCFG item);
        partial void OnAfterSysCFGCreated(SQLFlowUi.Models.sqlflowProd.SysCFG item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCFG> CreateSysCFG(SQLFlowUi.Models.sqlflowProd.SysCFG syscfg)
        {
            OnSysCFGCreated(syscfg);

            var existingItem = Context.SysCFG
                              .Where(i => i.ParamName == syscfg.ParamName)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysCFG.Add(syscfg);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syscfg).State = EntityState.Detached;
                throw;
            }

            OnAfterSysCFGCreated(syscfg);

            return syscfg;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCFG> CancelSysCFGChanges(SQLFlowUi.Models.sqlflowProd.SysCFG item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysCFGUpdated(SQLFlowUi.Models.sqlflowProd.SysCFG item);
        partial void OnAfterSysCFGUpdated(SQLFlowUi.Models.sqlflowProd.SysCFG item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCFG> UpdateSysCFG(string paramname, SQLFlowUi.Models.sqlflowProd.SysCFG syscfg)
        {
            OnSysCFGUpdated(syscfg);

            var itemToUpdate = Context.SysCFG
                              .Where(i => i.ParamName == syscfg.ParamName)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syscfg);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysCFGUpdated(syscfg);

            return syscfg;
        }

        partial void OnSysCFGDeleted(SQLFlowUi.Models.sqlflowProd.SysCFG item);
        partial void OnAfterSysCFGDeleted(SQLFlowUi.Models.sqlflowProd.SysCFG item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCFG> DeleteSysCFG(string paramname)
        {
            var itemToDelete = Context.SysCFG
                              .Where(i => i.ParamName == paramname)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysCFGDeleted(itemToDelete);


            Context.SysCFG.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysCFGDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysCheckDataTypesToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syscheckdatatypes/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syscheckdatatypes/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysCheckDataTypesToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syscheckdatatypes/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syscheckdatatypes/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysCheckDataTypesRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes>> GetSysCheckDataTypes(Query query = null)
        {
            var items = Context.SysCheckDataTypes.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysCheckDataTypesRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysCheckDataTypesGet(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item);
        partial void OnGetSysCheckDataTypesByRecId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> GetSysCheckDataTypesByRecId(int recid)
        {
            var items = Context.SysCheckDataTypes
                              .AsNoTracking()
                              .Where(i => i.RecID == recid);

 
            OnGetSysCheckDataTypesByRecId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysCheckDataTypesGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysCheckDataTypesCreated(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item);
        partial void OnAfterSysCheckDataTypesCreated(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> CreateSysCheckDataTypes(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes syscheckdatatypes)
        {
            OnSysCheckDataTypesCreated(syscheckdatatypes);

            var existingItem = Context.SysCheckDataTypes
                              .Where(i => i.RecID == syscheckdatatypes.RecID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysCheckDataTypes.Add(syscheckdatatypes);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syscheckdatatypes).State = EntityState.Detached;
                throw;
            }

            OnAfterSysCheckDataTypesCreated(syscheckdatatypes);

            return syscheckdatatypes;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> CancelSysCheckDataTypesChanges(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysCheckDataTypesUpdated(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item);
        partial void OnAfterSysCheckDataTypesUpdated(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> UpdateSysCheckDataTypes(int recid, SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes syscheckdatatypes)
        {
            OnSysCheckDataTypesUpdated(syscheckdatatypes);

            var itemToUpdate = Context.SysCheckDataTypes
                              .Where(i => i.RecID == syscheckdatatypes.RecID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syscheckdatatypes);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysCheckDataTypesUpdated(syscheckdatatypes);

            return syscheckdatatypes;
        }

        partial void OnSysCheckDataTypesDeleted(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item);
        partial void OnAfterSysCheckDataTypesDeleted(SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysCheckDataTypes> DeleteSysCheckDataTypes(int recid)
        {
            var itemToDelete = Context.SysCheckDataTypes
                              .Where(i => i.RecID == recid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysCheckDataTypesDeleted(itemToDelete);


            Context.SysCheckDataTypes.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysCheckDataTypesDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysColumnToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syscolumn/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syscolumn/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysColumnToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syscolumn/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syscolumn/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysColumnRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysColumn> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysColumn>> GetSysColumn(Query query = null)
        {
            var items = Context.SysColumn.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysColumnRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysColumnGet(SQLFlowUi.Models.sqlflowProd.SysColumn item);
        partial void OnGetSysColumnBySysColumnId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysColumn> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysColumn> GetSysColumnBySysColumnId(int syscolumnid)
        {
            var items = Context.SysColumn
                              .AsNoTracking()
                              .Where(i => i.SysColumnID == syscolumnid);

 
            OnGetSysColumnBySysColumnId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysColumnGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysColumnCreated(SQLFlowUi.Models.sqlflowProd.SysColumn item);
        partial void OnAfterSysColumnCreated(SQLFlowUi.Models.sqlflowProd.SysColumn item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysColumn> CreateSysColumn(SQLFlowUi.Models.sqlflowProd.SysColumn syscolumn)
        {
            OnSysColumnCreated(syscolumn);

            var existingItem = Context.SysColumn
                              .Where(i => i.SysColumnID == syscolumn.SysColumnID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysColumn.Add(syscolumn);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syscolumn).State = EntityState.Detached;
                throw;
            }

            OnAfterSysColumnCreated(syscolumn);

            return syscolumn;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysColumn> CancelSysColumnChanges(SQLFlowUi.Models.sqlflowProd.SysColumn item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysColumnUpdated(SQLFlowUi.Models.sqlflowProd.SysColumn item);
        partial void OnAfterSysColumnUpdated(SQLFlowUi.Models.sqlflowProd.SysColumn item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysColumn> UpdateSysColumn(int syscolumnid, SQLFlowUi.Models.sqlflowProd.SysColumn syscolumn)
        {
            OnSysColumnUpdated(syscolumn);

            var itemToUpdate = Context.SysColumn
                              .Where(i => i.SysColumnID == syscolumn.SysColumnID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syscolumn);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysColumnUpdated(syscolumn);

            return syscolumn;
        }

        partial void OnSysColumnDeleted(SQLFlowUi.Models.sqlflowProd.SysColumn item);
        partial void OnAfterSysColumnDeleted(SQLFlowUi.Models.sqlflowProd.SysColumn item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysColumn> DeleteSysColumn(int syscolumnid)
        {
            var itemToDelete = Context.SysColumn
                              .Where(i => i.SysColumnID == syscolumnid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysColumnDeleted(itemToDelete);


            Context.SysColumn.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysColumnDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysDataSourceToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdatasource/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdatasource/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysDataSourceToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdatasource/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdatasource/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysDataSourceRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource>> GetSysDataSource(Query query = null)
        {
            var items = Context.SysDataSource.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDataSourceRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysDataSourceGet(SQLFlowUi.Models.sqlflowProd.SysDataSource item);
        partial void OnGetSysDataSourceByDataSourceId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysDataSource> GetSysDataSourceByDataSourceId(int datasourceid)
        {
            var items = Context.SysDataSource
                              .AsNoTracking()
                              .Where(i => i.DataSourceID == datasourceid);

 
            OnGetSysDataSourceByDataSourceId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysDataSourceGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysDataSourceCreated(SQLFlowUi.Models.sqlflowProd.SysDataSource item);
        partial void OnAfterSysDataSourceCreated(SQLFlowUi.Models.sqlflowProd.SysDataSource item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDataSource> CreateSysDataSource(SQLFlowUi.Models.sqlflowProd.SysDataSource sysdatasource)
        {
            OnSysDataSourceCreated(sysdatasource);

            var existingItem = Context.SysDataSource
                              .Where(i => i.DataSourceID == sysdatasource.DataSourceID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysDataSource.Add(sysdatasource);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysdatasource).State = EntityState.Detached;
                throw;
            }

            OnAfterSysDataSourceCreated(sysdatasource);

            return sysdatasource;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDataSource> CancelSysDataSourceChanges(SQLFlowUi.Models.sqlflowProd.SysDataSource item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysDataSourceUpdated(SQLFlowUi.Models.sqlflowProd.SysDataSource item);
        partial void OnAfterSysDataSourceUpdated(SQLFlowUi.Models.sqlflowProd.SysDataSource item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDataSource> UpdateSysDataSource(int datasourceid, SQLFlowUi.Models.sqlflowProd.SysDataSource sysdatasource)
        {
            OnSysDataSourceUpdated(sysdatasource);

            var itemToUpdate = Context.SysDataSource
                              .Where(i => i.DataSourceID == sysdatasource.DataSourceID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysdatasource);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysDataSourceUpdated(sysdatasource);

            return sysdatasource;
        }

        partial void OnSysDataSourceDeleted(SQLFlowUi.Models.sqlflowProd.SysDataSource item);
        partial void OnAfterSysDataSourceDeleted(SQLFlowUi.Models.sqlflowProd.SysDataSource item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDataSource> DeleteSysDataSource(int datasourceid)
        {
            var itemToDelete = Context.SysDataSource
                              .Where(i => i.DataSourceID == datasourceid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysDataSourceDeleted(itemToDelete);


            Context.SysDataSource.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysDataSourceDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysDateTimeFormatToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdatetimeformat/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdatetimeformat/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysDateTimeFormatToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdatetimeformat/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdatetimeformat/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysDateTimeFormatRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat>> GetSysDateTimeFormat(Query query = null)
        {
            var items = Context.SysDateTimeFormat.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDateTimeFormatRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysDateTimeFormatGet(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item);
        partial void OnGetSysDateTimeFormatByFormatId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> GetSysDateTimeFormatByFormatId(int formatid)
        {
            var items = Context.SysDateTimeFormat
                              .AsNoTracking()
                              .Where(i => i.FormatID == formatid);

 
            OnGetSysDateTimeFormatByFormatId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysDateTimeFormatGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysDateTimeFormatCreated(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item);
        partial void OnAfterSysDateTimeFormatCreated(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> CreateSysDateTimeFormat(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat sysdatetimeformat)
        {
            OnSysDateTimeFormatCreated(sysdatetimeformat);

            var existingItem = Context.SysDateTimeFormat
                              .Where(i => i.FormatID == sysdatetimeformat.FormatID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysDateTimeFormat.Add(sysdatetimeformat);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysdatetimeformat).State = EntityState.Detached;
                throw;
            }

            OnAfterSysDateTimeFormatCreated(sysdatetimeformat);

            return sysdatetimeformat;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> CancelSysDateTimeFormatChanges(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysDateTimeFormatUpdated(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item);
        partial void OnAfterSysDateTimeFormatUpdated(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> UpdateSysDateTimeFormat(int formatid, SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat sysdatetimeformat)
        {
            OnSysDateTimeFormatUpdated(sysdatetimeformat);

            var itemToUpdate = Context.SysDateTimeFormat
                              .Where(i => i.FormatID == sysdatetimeformat.FormatID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysdatetimeformat);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysDateTimeFormatUpdated(sysdatetimeformat);

            return sysdatetimeformat;
        }

        partial void OnSysDateTimeFormatDeleted(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item);
        partial void OnAfterSysDateTimeFormatDeleted(SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> DeleteSysDateTimeFormat(int formatid)
        {
            var itemToDelete = Context.SysDateTimeFormat
                              .Where(i => i.FormatID == formatid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysDateTimeFormatDeleted(itemToDelete);


            Context.SysDateTimeFormat.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysDateTimeFormatDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysDateTimeStyleToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdatetimestyle/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdatetimestyle/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysDateTimeStyleToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdatetimestyle/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdatetimestyle/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysDateTimeStyleRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDateTimeStyle> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDateTimeStyle>> GetSysDateTimeStyle(Query query = null)
        {
            var items = Context.SysDateTimeStyle.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDateTimeStyleRead(ref items);

            return await Task.FromResult(items);
        }

        public async Task ExportSysDocToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdoc/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdoc/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysDocToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdoc/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdoc/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysDocRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDoc> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDoc>> GetSysDoc(Query query = null)
        {
            var items = Context.SysDoc.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDocRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysDocGet(SQLFlowUi.Models.sqlflowProd.SysDoc item);
        partial void OnGetSysDocBySysDocId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDoc> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysDoc> GetSysDocBySysDocId(int sysdocid)
        {
            var items = Context.SysDoc
                              .AsNoTracking()
                              .Where(i => i.SysDocID == sysdocid);

 
            OnGetSysDocBySysDocId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysDocGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysDocCreated(SQLFlowUi.Models.sqlflowProd.SysDoc item);
        partial void OnAfterSysDocCreated(SQLFlowUi.Models.sqlflowProd.SysDoc item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDoc> CreateSysDoc(SQLFlowUi.Models.sqlflowProd.SysDoc sysdoc)
        {
            OnSysDocCreated(sysdoc);

            var existingItem = Context.SysDoc
                              .Where(i => i.SysDocID == sysdoc.SysDocID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysDoc.Add(sysdoc);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysdoc).State = EntityState.Detached;
                throw;
            }

            OnAfterSysDocCreated(sysdoc);

            return sysdoc;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDoc> CancelSysDocChanges(SQLFlowUi.Models.sqlflowProd.SysDoc item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysDocUpdated(SQLFlowUi.Models.sqlflowProd.SysDoc item);
        partial void OnAfterSysDocUpdated(SQLFlowUi.Models.sqlflowProd.SysDoc item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDoc> UpdateSysDoc(int sysdocid, SQLFlowUi.Models.sqlflowProd.SysDoc sysdoc)
        {
            OnSysDocUpdated(sysdoc);

            var itemToUpdate = Context.SysDoc
                              .Where(i => i.SysDocID == sysdoc.SysDocID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysdoc);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysDocUpdated(sysdoc);

            return sysdoc;
        }

        partial void OnSysDocDeleted(SQLFlowUi.Models.sqlflowProd.SysDoc item);
        partial void OnAfterSysDocDeleted(SQLFlowUi.Models.sqlflowProd.SysDoc item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDoc> DeleteSysDoc(int sysdocid)
        {
            var itemToDelete = Context.SysDoc
                              .Where(i => i.SysDocID == sysdocid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysDocDeleted(itemToDelete);


            Context.SysDoc.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysDocDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysDocNoteToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdocnote/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdocnote/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysDocNoteToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdocnote/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdocnote/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysDocNoteRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocNote> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocNote>> GetSysDocNote(Query query = null)
        {
            var items = Context.SysDocNote.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDocNoteRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysDocNoteGet(SQLFlowUi.Models.sqlflowProd.SysDocNote item);
        partial void OnGetSysDocNoteByDocNoteId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocNote> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocNote> GetSysDocNoteByDocNoteId(int docnoteid)
        {
            var items = Context.SysDocNote
                              .AsNoTracking()
                              .Where(i => i.DocNoteID == docnoteid);

 
            OnGetSysDocNoteByDocNoteId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysDocNoteGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysDocNoteCreated(SQLFlowUi.Models.sqlflowProd.SysDocNote item);
        partial void OnAfterSysDocNoteCreated(SQLFlowUi.Models.sqlflowProd.SysDocNote item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocNote> CreateSysDocNote(SQLFlowUi.Models.sqlflowProd.SysDocNote sysdocnote)
        {
            OnSysDocNoteCreated(sysdocnote);

            var existingItem = Context.SysDocNote
                              .Where(i => i.DocNoteID == sysdocnote.DocNoteID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysDocNote.Add(sysdocnote);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysdocnote).State = EntityState.Detached;
                throw;
            }

            OnAfterSysDocNoteCreated(sysdocnote);

            return sysdocnote;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocNote> CancelSysDocNoteChanges(SQLFlowUi.Models.sqlflowProd.SysDocNote item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysDocNoteUpdated(SQLFlowUi.Models.sqlflowProd.SysDocNote item);
        partial void OnAfterSysDocNoteUpdated(SQLFlowUi.Models.sqlflowProd.SysDocNote item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocNote> UpdateSysDocNote(int docnoteid, SQLFlowUi.Models.sqlflowProd.SysDocNote sysdocnote)
        {
            OnSysDocNoteUpdated(sysdocnote);

            var itemToUpdate = Context.SysDocNote
                              .Where(i => i.DocNoteID == sysdocnote.DocNoteID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysdocnote);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysDocNoteUpdated(sysdocnote);

            return sysdocnote;
        }

        partial void OnSysDocNoteDeleted(SQLFlowUi.Models.sqlflowProd.SysDocNote item);
        partial void OnAfterSysDocNoteDeleted(SQLFlowUi.Models.sqlflowProd.SysDocNote item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocNote> DeleteSysDocNote(int docnoteid)
        {
            var itemToDelete = Context.SysDocNote
                              .Where(i => i.DocNoteID == docnoteid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysDocNoteDeleted(itemToDelete);


            Context.SysDocNote.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysDocNoteDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysDocRelationToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdocrelation/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdocrelation/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysDocRelationToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysdocrelation/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysdocrelation/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysDocRelationRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocRelation> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocRelation>> GetSysDocRelation(Query query = null)
        {
            var items = Context.SysDocRelation.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDocRelationRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysDocRelationGet(SQLFlowUi.Models.sqlflowProd.SysDocRelation item);
        partial void OnGetSysDocRelationByRelationId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocRelation> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocRelation> GetSysDocRelationByRelationId(int relationid)
        {
            var items = Context.SysDocRelation
                              .AsNoTracking()
                              .Where(i => i.RelationID == relationid);

 
            OnGetSysDocRelationByRelationId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysDocRelationGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysDocRelationCreated(SQLFlowUi.Models.sqlflowProd.SysDocRelation item);
        partial void OnAfterSysDocRelationCreated(SQLFlowUi.Models.sqlflowProd.SysDocRelation item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocRelation> CreateSysDocRelation(SQLFlowUi.Models.sqlflowProd.SysDocRelation sysdocrelation)
        {
            OnSysDocRelationCreated(sysdocrelation);

            var existingItem = Context.SysDocRelation
                              .Where(i => i.RelationID == sysdocrelation.RelationID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysDocRelation.Add(sysdocrelation);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysdocrelation).State = EntityState.Detached;
                throw;
            }

            OnAfterSysDocRelationCreated(sysdocrelation);

            return sysdocrelation;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocRelation> CancelSysDocRelationChanges(SQLFlowUi.Models.sqlflowProd.SysDocRelation item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysDocRelationUpdated(SQLFlowUi.Models.sqlflowProd.SysDocRelation item);
        partial void OnAfterSysDocRelationUpdated(SQLFlowUi.Models.sqlflowProd.SysDocRelation item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocRelation> UpdateSysDocRelation(int relationid, SQLFlowUi.Models.sqlflowProd.SysDocRelation sysdocrelation)
        {
            OnSysDocRelationUpdated(sysdocrelation);

            var itemToUpdate = Context.SysDocRelation
                              .Where(i => i.RelationID == sysdocrelation.RelationID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysdocrelation);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysDocRelationUpdated(sysdocrelation);

            return sysdocrelation;
        }

        partial void OnSysDocRelationDeleted(SQLFlowUi.Models.sqlflowProd.SysDocRelation item);
        partial void OnAfterSysDocRelationDeleted(SQLFlowUi.Models.sqlflowProd.SysDocRelation item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysDocRelation> DeleteSysDocRelation(int relationid)
        {
            var itemToDelete = Context.SysDocRelation
                              .Where(i => i.RelationID == relationid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysDocRelationDeleted(itemToDelete);


            Context.SysDocRelation.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysDocRelationDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysErrorToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syserror/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syserror/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysErrorToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syserror/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syserror/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysErrorRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysError> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysError>> GetSysError(Query query = null)
        {
            var items = Context.SysError.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysErrorRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysErrorGet(SQLFlowUi.Models.sqlflowProd.SysError item);
        partial void OnGetSysErrorByErrorId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysError> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysError> GetSysErrorByErrorId(int errorid)
        {
            var items = Context.SysError
                              .AsNoTracking()
                              .Where(i => i.ErrorID == errorid);

 
            OnGetSysErrorByErrorId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysErrorGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysErrorCreated(SQLFlowUi.Models.sqlflowProd.SysError item);
        partial void OnAfterSysErrorCreated(SQLFlowUi.Models.sqlflowProd.SysError item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysError> CreateSysError(SQLFlowUi.Models.sqlflowProd.SysError syserror)
        {
            OnSysErrorCreated(syserror);

            var existingItem = Context.SysError
                              .Where(i => i.ErrorID == syserror.ErrorID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysError.Add(syserror);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syserror).State = EntityState.Detached;
                throw;
            }

            OnAfterSysErrorCreated(syserror);

            return syserror;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysError> CancelSysErrorChanges(SQLFlowUi.Models.sqlflowProd.SysError item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysErrorUpdated(SQLFlowUi.Models.sqlflowProd.SysError item);
        partial void OnAfterSysErrorUpdated(SQLFlowUi.Models.sqlflowProd.SysError item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysError> UpdateSysError(int errorid, SQLFlowUi.Models.sqlflowProd.SysError syserror)
        {
            OnSysErrorUpdated(syserror);

            var itemToUpdate = Context.SysError
                              .Where(i => i.ErrorID == syserror.ErrorID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syserror);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysErrorUpdated(syserror);

            return syserror;
        }

        partial void OnSysErrorDeleted(SQLFlowUi.Models.sqlflowProd.SysError item);
        partial void OnAfterSysErrorDeleted(SQLFlowUi.Models.sqlflowProd.SysError item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysError> DeleteSysError(int errorid)
        {
            var itemToDelete = Context.SysError
                              .Where(i => i.ErrorID == errorid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysErrorDeleted(itemToDelete);


            Context.SysError.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysErrorDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysFlowDepToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysflowdep/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysflowdep/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysFlowDepToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysflowdep/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysflowdep/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysFlowDepRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowDep> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowDep>> GetSysFlowDep(Query query = null)
        {
            var items = Context.SysFlowDep.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysFlowDepRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysFlowDepGet(SQLFlowUi.Models.sqlflowProd.SysFlowDep item);
        partial void OnGetSysFlowDepByRecId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowDep> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowDep> GetSysFlowDepByRecId(int recid)
        {
            var items = Context.SysFlowDep
                              .AsNoTracking()
                              .Where(i => i.RecID == recid);

 
            OnGetSysFlowDepByRecId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysFlowDepGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysFlowDepCreated(SQLFlowUi.Models.sqlflowProd.SysFlowDep item);
        partial void OnAfterSysFlowDepCreated(SQLFlowUi.Models.sqlflowProd.SysFlowDep item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowDep> CreateSysFlowDep(SQLFlowUi.Models.sqlflowProd.SysFlowDep sysflowdep)
        {
            OnSysFlowDepCreated(sysflowdep);

            var existingItem = Context.SysFlowDep
                              .Where(i => i.RecID == sysflowdep.RecID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysFlowDep.Add(sysflowdep);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysflowdep).State = EntityState.Detached;
                throw;
            }

            OnAfterSysFlowDepCreated(sysflowdep);

            return sysflowdep;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowDep> CancelSysFlowDepChanges(SQLFlowUi.Models.sqlflowProd.SysFlowDep item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysFlowDepUpdated(SQLFlowUi.Models.sqlflowProd.SysFlowDep item);
        partial void OnAfterSysFlowDepUpdated(SQLFlowUi.Models.sqlflowProd.SysFlowDep item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowDep> UpdateSysFlowDep(int recid, SQLFlowUi.Models.sqlflowProd.SysFlowDep sysflowdep)
        {
            OnSysFlowDepUpdated(sysflowdep);

            var itemToUpdate = Context.SysFlowDep
                              .Where(i => i.RecID == sysflowdep.RecID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysflowdep);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysFlowDepUpdated(sysflowdep);

            return sysflowdep;
        }

        partial void OnSysFlowDepDeleted(SQLFlowUi.Models.sqlflowProd.SysFlowDep item);
        partial void OnAfterSysFlowDepDeleted(SQLFlowUi.Models.sqlflowProd.SysFlowDep item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowDep> DeleteSysFlowDep(int recid)
        {
            var itemToDelete = Context.SysFlowDep
                              .Where(i => i.RecID == recid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysFlowDepDeleted(itemToDelete);


            Context.SysFlowDep.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysFlowDepDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysFlowNoteToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysflownote/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysflownote/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysFlowNoteToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysflownote/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysflownote/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysFlowNoteRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowNote> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowNote>> GetSysFlowNote(Query query = null)
        {
            var items = Context.SysFlowNote.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysFlowNoteRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysFlowNoteGet(SQLFlowUi.Models.sqlflowProd.SysFlowNote item);
        partial void OnGetSysFlowNoteByFlowNoteId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowNote> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowNote> GetSysFlowNoteByFlowNoteId(int flownoteid)
        {
            var items = Context.SysFlowNote
                              .AsNoTracking()
                              .Where(i => i.FlowNoteID == flownoteid);

 
            OnGetSysFlowNoteByFlowNoteId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysFlowNoteGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysFlowNoteCreated(SQLFlowUi.Models.sqlflowProd.SysFlowNote item);
        partial void OnAfterSysFlowNoteCreated(SQLFlowUi.Models.sqlflowProd.SysFlowNote item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowNote> CreateSysFlowNote(SQLFlowUi.Models.sqlflowProd.SysFlowNote sysflownote)
        {
            OnSysFlowNoteCreated(sysflownote);

            var existingItem = Context.SysFlowNote
                              .Where(i => i.FlowNoteID == sysflownote.FlowNoteID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysFlowNote.Add(sysflownote);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysflownote).State = EntityState.Detached;
                throw;
            }

            OnAfterSysFlowNoteCreated(sysflownote);

            return sysflownote;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowNote> CancelSysFlowNoteChanges(SQLFlowUi.Models.sqlflowProd.SysFlowNote item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysFlowNoteUpdated(SQLFlowUi.Models.sqlflowProd.SysFlowNote item);
        partial void OnAfterSysFlowNoteUpdated(SQLFlowUi.Models.sqlflowProd.SysFlowNote item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowNote> UpdateSysFlowNote(int flownoteid, SQLFlowUi.Models.sqlflowProd.SysFlowNote sysflownote)
        {
            OnSysFlowNoteUpdated(sysflownote);

            var itemToUpdate = Context.SysFlowNote
                              .Where(i => i.FlowNoteID == sysflownote.FlowNoteID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysflownote);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysFlowNoteUpdated(sysflownote);

            return sysflownote;
        }

        partial void OnSysFlowNoteDeleted(SQLFlowUi.Models.sqlflowProd.SysFlowNote item);
        partial void OnAfterSysFlowNoteDeleted(SQLFlowUi.Models.sqlflowProd.SysFlowNote item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysFlowNote> DeleteSysFlowNote(int flownoteid)
        {
            var itemToDelete = Context.SysFlowNote
                              .Where(i => i.FlowNoteID == flownoteid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysFlowNoteDeleted(itemToDelete);


            Context.SysFlowNote.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysFlowNoteDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysLogToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslog/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslog/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysLogToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslog/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslog/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysLogRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLog> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysLog>> GetSysLog(Query query = null)
        {
            var items = Context.SysLog.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysLogRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysLogGet(SQLFlowUi.Models.sqlflowProd.SysLog item);
        partial void OnGetSysLogByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLog> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysLog> GetSysLogByFlowId(int flowid)
        {
            var items = Context.SysLog
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);

 
            OnGetSysLogByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysLogCreated(SQLFlowUi.Models.sqlflowProd.SysLog item);
        partial void OnAfterSysLogCreated(SQLFlowUi.Models.sqlflowProd.SysLog item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLog> CreateSysLog(SQLFlowUi.Models.sqlflowProd.SysLog syslog)
        {
            OnSysLogCreated(syslog);

            var existingItem = Context.SysLog
                              .Where(i => i.FlowID == syslog.FlowID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysLog.Add(syslog);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syslog).State = EntityState.Detached;
                throw;
            }

            OnAfterSysLogCreated(syslog);

            return syslog;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLog> CancelSysLogChanges(SQLFlowUi.Models.sqlflowProd.SysLog item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysLogUpdated(SQLFlowUi.Models.sqlflowProd.SysLog item);
        partial void OnAfterSysLogUpdated(SQLFlowUi.Models.sqlflowProd.SysLog item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLog> UpdateSysLog(int flowid, SQLFlowUi.Models.sqlflowProd.SysLog syslog)
        {
            OnSysLogUpdated(syslog);

            var itemToUpdate = Context.SysLog
                              .Where(i => i.FlowID == syslog.FlowID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syslog);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysLogUpdated(syslog);

            return syslog;
        }

        partial void OnSysLogDeleted(SQLFlowUi.Models.sqlflowProd.SysLog item);
        partial void OnAfterSysLogDeleted(SQLFlowUi.Models.sqlflowProd.SysLog item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLog> DeleteSysLog(int flowid)
        {
            var itemToDelete = Context.SysLog
                              .Where(i => i.FlowID == flowid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysLogDeleted(itemToDelete);


            Context.SysLog.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysLogDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysLogAssertionToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogassertion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogassertion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysLogAssertionToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogassertion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogassertion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysLogAssertionRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogAssertion>> GetSysLogAssertion(Query query = null)
        {
            var items = Context.SysLogAssertion.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysLogAssertionRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysLogAssertionGet(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item);
        partial void OnGetSysLogAssertionByRecId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> GetSysLogAssertionByRecId(int recid)
        {
            var items = Context.SysLogAssertion
                              .AsNoTracking()
                              .Where(i => i.RecID == recid);

 
            OnGetSysLogAssertionByRecId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogAssertionGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysLogAssertionCreated(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item);
        partial void OnAfterSysLogAssertionCreated(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> CreateSysLogAssertion(SQLFlowUi.Models.sqlflowProd.SysLogAssertion syslogassertion)
        {
            OnSysLogAssertionCreated(syslogassertion);

            var existingItem = Context.SysLogAssertion
                              .Where(i => i.RecID == syslogassertion.RecID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysLogAssertion.Add(syslogassertion);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syslogassertion).State = EntityState.Detached;
                throw;
            }

            OnAfterSysLogAssertionCreated(syslogassertion);

            return syslogassertion;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> CancelSysLogAssertionChanges(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysLogAssertionUpdated(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item);
        partial void OnAfterSysLogAssertionUpdated(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> UpdateSysLogAssertion(int recid, SQLFlowUi.Models.sqlflowProd.SysLogAssertion syslogassertion)
        {
            OnSysLogAssertionUpdated(syslogassertion);

            var itemToUpdate = Context.SysLogAssertion
                              .Where(i => i.RecID == syslogassertion.RecID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syslogassertion);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysLogAssertionUpdated(syslogassertion);

            return syslogassertion;
        }

        partial void OnSysLogAssertionDeleted(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item);
        partial void OnAfterSysLogAssertionDeleted(SQLFlowUi.Models.sqlflowProd.SysLogAssertion item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> DeleteSysLogAssertion(int recid)
        {
            var itemToDelete = Context.SysLogAssertion
                              .Where(i => i.RecID == recid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysLogAssertionDeleted(itemToDelete);


            Context.SysLogAssertion.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysLogAssertionDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysLogBatchToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogbatch/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogbatch/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysLogBatchToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogbatch/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogbatch/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysLogBatchRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogBatch> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogBatch>> GetSysLogBatch(Query query = null)
        {
            var items = Context.SysLogBatch.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysLogBatchRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysLogBatchGet(SQLFlowUi.Models.sqlflowProd.SysLogBatch item);
        partial void OnGetSysLogBatchByRecId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogBatch> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogBatch> GetSysLogBatchByRecId(int recid)
        {
            var items = Context.SysLogBatch
                              .AsNoTracking()
                              .Where(i => i.RecID == recid);

 
            OnGetSysLogBatchByRecId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogBatchGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysLogBatchCreated(SQLFlowUi.Models.sqlflowProd.SysLogBatch item);
        partial void OnAfterSysLogBatchCreated(SQLFlowUi.Models.sqlflowProd.SysLogBatch item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogBatch> CreateSysLogBatch(SQLFlowUi.Models.sqlflowProd.SysLogBatch syslogbatch)
        {
            OnSysLogBatchCreated(syslogbatch);

            var existingItem = Context.SysLogBatch
                              .Where(i => i.RecID == syslogbatch.RecID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysLogBatch.Add(syslogbatch);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syslogbatch).State = EntityState.Detached;
                throw;
            }

            OnAfterSysLogBatchCreated(syslogbatch);

            return syslogbatch;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogBatch> CancelSysLogBatchChanges(SQLFlowUi.Models.sqlflowProd.SysLogBatch item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysLogBatchUpdated(SQLFlowUi.Models.sqlflowProd.SysLogBatch item);
        partial void OnAfterSysLogBatchUpdated(SQLFlowUi.Models.sqlflowProd.SysLogBatch item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogBatch> UpdateSysLogBatch(int recid, SQLFlowUi.Models.sqlflowProd.SysLogBatch syslogbatch)
        {
            OnSysLogBatchUpdated(syslogbatch);

            var itemToUpdate = Context.SysLogBatch
                              .Where(i => i.RecID == syslogbatch.RecID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syslogbatch);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysLogBatchUpdated(syslogbatch);

            return syslogbatch;
        }

        partial void OnSysLogBatchDeleted(SQLFlowUi.Models.sqlflowProd.SysLogBatch item);
        partial void OnAfterSysLogBatchDeleted(SQLFlowUi.Models.sqlflowProd.SysLogBatch item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogBatch> DeleteSysLogBatch(int recid)
        {
            var itemToDelete = Context.SysLogBatch
                              .Where(i => i.RecID == recid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysLogBatchDeleted(itemToDelete);


            Context.SysLogBatch.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysLogBatchDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysLogExportToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogexport/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogexport/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysLogExportToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogexport/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogexport/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysLogExportRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogExport> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogExport>> GetSysLogExport(Query query = null)
        {
            var items = Context.SysLogExport.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysLogExportRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysLogExportGet(SQLFlowUi.Models.sqlflowProd.SysLogExport item);
        partial void OnGetSysLogExportByRecId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogExport> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogExport> GetSysLogExportByRecId(int recid)
        {
            var items = Context.SysLogExport
                              .AsNoTracking()
                              .Where(i => i.RecID == recid);

 
            OnGetSysLogExportByRecId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogExportGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysLogExportCreated(SQLFlowUi.Models.sqlflowProd.SysLogExport item);
        partial void OnAfterSysLogExportCreated(SQLFlowUi.Models.sqlflowProd.SysLogExport item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogExport> CreateSysLogExport(SQLFlowUi.Models.sqlflowProd.SysLogExport syslogexport)
        {
            OnSysLogExportCreated(syslogexport);

            var existingItem = Context.SysLogExport
                              .Where(i => i.RecID == syslogexport.RecID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysLogExport.Add(syslogexport);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syslogexport).State = EntityState.Detached;
                throw;
            }

            OnAfterSysLogExportCreated(syslogexport);

            return syslogexport;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogExport> CancelSysLogExportChanges(SQLFlowUi.Models.sqlflowProd.SysLogExport item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysLogExportUpdated(SQLFlowUi.Models.sqlflowProd.SysLogExport item);
        partial void OnAfterSysLogExportUpdated(SQLFlowUi.Models.sqlflowProd.SysLogExport item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogExport> UpdateSysLogExport(int recid, SQLFlowUi.Models.sqlflowProd.SysLogExport syslogexport)
        {
            OnSysLogExportUpdated(syslogexport);

            var itemToUpdate = Context.SysLogExport
                              .Where(i => i.RecID == syslogexport.RecID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syslogexport);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysLogExportUpdated(syslogexport);

            return syslogexport;
        }

        partial void OnSysLogExportDeleted(SQLFlowUi.Models.sqlflowProd.SysLogExport item);
        partial void OnAfterSysLogExportDeleted(SQLFlowUi.Models.sqlflowProd.SysLogExport item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogExport> DeleteSysLogExport(int recid)
        {
            var itemToDelete = Context.SysLogExport
                              .Where(i => i.RecID == recid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysLogExportDeleted(itemToDelete);


            Context.SysLogExport.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysLogExportDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysLogFileToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogfile/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogfile/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysLogFileToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogfile/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogfile/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysLogFileRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogFile> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogFile>> GetSysLogFile(Query query = null)
        {
            var items = Context.SysLogFile.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysLogFileRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysLogFileGet(SQLFlowUi.Models.sqlflowProd.SysLogFile item);
        partial void OnGetSysLogFileByLogFileId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogFile> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFile> GetSysLogFileByLogFileId(int logfileid)
        {
            var items = Context.SysLogFile
                              .AsNoTracking()
                              .Where(i => i.LogFileID == logfileid);

 
            OnGetSysLogFileByLogFileId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogFileGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysLogFileCreated(SQLFlowUi.Models.sqlflowProd.SysLogFile item);
        partial void OnAfterSysLogFileCreated(SQLFlowUi.Models.sqlflowProd.SysLogFile item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFile> CreateSysLogFile(SQLFlowUi.Models.sqlflowProd.SysLogFile syslogfile)
        {
            OnSysLogFileCreated(syslogfile);

            var existingItem = Context.SysLogFile
                              .Where(i => i.LogFileID == syslogfile.LogFileID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysLogFile.Add(syslogfile);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syslogfile).State = EntityState.Detached;
                throw;
            }

            OnAfterSysLogFileCreated(syslogfile);

            return syslogfile;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFile> CancelSysLogFileChanges(SQLFlowUi.Models.sqlflowProd.SysLogFile item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysLogFileUpdated(SQLFlowUi.Models.sqlflowProd.SysLogFile item);
        partial void OnAfterSysLogFileUpdated(SQLFlowUi.Models.sqlflowProd.SysLogFile item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFile> UpdateSysLogFile(int logfileid, SQLFlowUi.Models.sqlflowProd.SysLogFile syslogfile)
        {
            OnSysLogFileUpdated(syslogfile);

            var itemToUpdate = Context.SysLogFile
                              .Where(i => i.LogFileID == syslogfile.LogFileID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syslogfile);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysLogFileUpdated(syslogfile);

            return syslogfile;
        }

        partial void OnSysLogFileDeleted(SQLFlowUi.Models.sqlflowProd.SysLogFile item);
        partial void OnAfterSysLogFileDeleted(SQLFlowUi.Models.sqlflowProd.SysLogFile item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFile> DeleteSysLogFile(int logfileid)
        {
            var itemToDelete = Context.SysLogFile
                              .Where(i => i.LogFileID == logfileid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysLogFileDeleted(itemToDelete);


            Context.SysLogFile.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysLogFileDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysLogFileEventToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogfileevent/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogfileevent/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysLogFileEventToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogfileevent/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogfileevent/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysLogFileEventRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent>> GetSysLogFileEvent(Query query = null)
        {
            var items = Context.SysLogFileEvent.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysLogFileEventRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysLogFileEventGet(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item);
        partial void OnGetSysLogFileEventByLogFileEventId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent> GetSysLogFileEventByLogFileEventId(int logfileeventid)
        {
            var items = Context.SysLogFileEvent
                              .AsNoTracking()
                              .Where(i => i.LogFileEventID == logfileeventid);

 
            OnGetSysLogFileEventByLogFileEventId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogFileEventGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysLogFileEventCreated(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item);
        partial void OnAfterSysLogFileEventCreated(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent> CreateSysLogFileEvent(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent syslogfileevent)
        {
            OnSysLogFileEventCreated(syslogfileevent);

            var existingItem = Context.SysLogFileEvent
                              .Where(i => i.LogFileEventID == syslogfileevent.LogFileEventID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysLogFileEvent.Add(syslogfileevent);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syslogfileevent).State = EntityState.Detached;
                throw;
            }

            OnAfterSysLogFileEventCreated(syslogfileevent);

            return syslogfileevent;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent> CancelSysLogFileEventChanges(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysLogFileEventUpdated(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item);
        partial void OnAfterSysLogFileEventUpdated(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent> UpdateSysLogFileEvent(int logfileeventid, SQLFlowUi.Models.sqlflowProd.SysLogFileEvent syslogfileevent)
        {
            OnSysLogFileEventUpdated(syslogfileevent);

            var itemToUpdate = Context.SysLogFileEvent
                              .Where(i => i.LogFileEventID == syslogfileevent.LogFileEventID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syslogfileevent);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysLogFileEventUpdated(syslogfileevent);

            return syslogfileevent;
        }

        partial void OnSysLogFileEventDeleted(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item);
        partial void OnAfterSysLogFileEventDeleted(SQLFlowUi.Models.sqlflowProd.SysLogFileEvent item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFileEvent> DeleteSysLogFileEvent(int logfileeventid)
        {
            var itemToDelete = Context.SysLogFileEvent
                              .Where(i => i.LogFileEventID == logfileeventid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysLogFileEventDeleted(itemToDelete);


            Context.SysLogFileEvent.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysLogFileEventDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysLogMatchKeyToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogmatchkey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogmatchkey/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysLogMatchKeyToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syslogmatchkey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syslogmatchkey/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysLogMatchKeyRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey>> GetSysLogMatchKey(Query query = null)
        {
            var items = Context.SysLogMatchKey.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysLogMatchKeyRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysLogMatchKeyGet(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item);
        partial void OnGetSysLogMatchKeyBySysLogMatchKey1(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey> GetSysLogMatchKeyBySysLogMatchKey1(int syslogmatchkey1)
        {
            var items = Context.SysLogMatchKey
                              .AsNoTracking()
                              .Where(i => i.SysLogMatchKey1 == syslogmatchkey1);

 
            OnGetSysLogMatchKeyBySysLogMatchKey1(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogMatchKeyGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysLogMatchKeyCreated(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item);
        partial void OnAfterSysLogMatchKeyCreated(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey> CreateSysLogMatchKey(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey syslogmatchkey)
        {
            OnSysLogMatchKeyCreated(syslogmatchkey);

            var existingItem = Context.SysLogMatchKey
                              .Where(i => i.SysLogMatchKey1 == syslogmatchkey.SysLogMatchKey1)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysLogMatchKey.Add(syslogmatchkey);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syslogmatchkey).State = EntityState.Detached;
                throw;
            }

            OnAfterSysLogMatchKeyCreated(syslogmatchkey);

            return syslogmatchkey;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey> CancelSysLogMatchKeyChanges(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysLogMatchKeyUpdated(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item);
        partial void OnAfterSysLogMatchKeyUpdated(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey> UpdateSysLogMatchKey(int syslogmatchkey1, SQLFlowUi.Models.sqlflowProd.SysLogMatchKey syslogmatchkey)
        {
            OnSysLogMatchKeyUpdated(syslogmatchkey);

            var itemToUpdate = Context.SysLogMatchKey
                              .Where(i => i.SysLogMatchKey1 == syslogmatchkey.SysLogMatchKey1)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syslogmatchkey);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysLogMatchKeyUpdated(syslogmatchkey);

            return syslogmatchkey;
        }

        partial void OnSysLogMatchKeyDeleted(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item);
        partial void OnAfterSysLogMatchKeyDeleted(SQLFlowUi.Models.sqlflowProd.SysLogMatchKey item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogMatchKey> DeleteSysLogMatchKey(int syslogmatchkey1)
        {
            var itemToDelete = Context.SysLogMatchKey
                              .Where(i => i.SysLogMatchKey1 == syslogmatchkey1)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysLogMatchKeyDeleted(itemToDelete);


            Context.SysLogMatchKey.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysLogMatchKeyDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysPeriodToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysperiod/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysperiod/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysPeriodToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysperiod/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysperiod/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysPeriodRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysPeriod> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysPeriod>> GetSysPeriod(Query query = null)
        {
            var items = Context.SysPeriod.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysPeriodRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysPeriodGet(SQLFlowUi.Models.sqlflowProd.SysPeriod item);
        partial void OnGetSysPeriodByPeriodId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysPeriod> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysPeriod> GetSysPeriodByPeriodId(int periodid)
        {
            var items = Context.SysPeriod
                              .AsNoTracking()
                              .Where(i => i.PeriodID == periodid);

 
            OnGetSysPeriodByPeriodId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysPeriodGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysPeriodCreated(SQLFlowUi.Models.sqlflowProd.SysPeriod item);
        partial void OnAfterSysPeriodCreated(SQLFlowUi.Models.sqlflowProd.SysPeriod item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysPeriod> CreateSysPeriod(SQLFlowUi.Models.sqlflowProd.SysPeriod sysperiod)
        {
            OnSysPeriodCreated(sysperiod);

            var existingItem = Context.SysPeriod
                              .Where(i => i.PeriodID == sysperiod.PeriodID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysPeriod.Add(sysperiod);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysperiod).State = EntityState.Detached;
                throw;
            }

            OnAfterSysPeriodCreated(sysperiod);

            return sysperiod;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysPeriod> CancelSysPeriodChanges(SQLFlowUi.Models.sqlflowProd.SysPeriod item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysPeriodUpdated(SQLFlowUi.Models.sqlflowProd.SysPeriod item);
        partial void OnAfterSysPeriodUpdated(SQLFlowUi.Models.sqlflowProd.SysPeriod item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysPeriod> UpdateSysPeriod(int periodid, SQLFlowUi.Models.sqlflowProd.SysPeriod sysperiod)
        {
            OnSysPeriodUpdated(sysperiod);

            var itemToUpdate = Context.SysPeriod
                              .Where(i => i.PeriodID == sysperiod.PeriodID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysperiod);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysPeriodUpdated(sysperiod);

            return sysperiod;
        }

        partial void OnSysPeriodDeleted(SQLFlowUi.Models.sqlflowProd.SysPeriod item);
        partial void OnAfterSysPeriodDeleted(SQLFlowUi.Models.sqlflowProd.SysPeriod item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysPeriod> DeleteSysPeriod(int periodid)
        {
            var itemToDelete = Context.SysPeriod
                              .Where(i => i.PeriodID == periodid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysPeriodDeleted(itemToDelete);


            Context.SysPeriod.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysPeriodDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysServicePrincipalToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysserviceprincipal/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysserviceprincipal/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysServicePrincipalToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysserviceprincipal/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysserviceprincipal/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysServicePrincipalRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal>> GetSysServicePrincipal(Query query = null)
        {
            var items = Context.SysServicePrincipal.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysServicePrincipalRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysServicePrincipalGet(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item);
        partial void OnGetSysServicePrincipalByServicePrincipalId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> GetSysServicePrincipalByServicePrincipalId(int serviceprincipalid)
        {
            var items = Context.SysServicePrincipal
                              .AsNoTracking()
                              .Where(i => i.ServicePrincipalID == serviceprincipalid);

 
            OnGetSysServicePrincipalByServicePrincipalId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysServicePrincipalGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysServicePrincipalCreated(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item);
        partial void OnAfterSysServicePrincipalCreated(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> CreateSysServicePrincipal(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal sysserviceprincipal)
        {
            OnSysServicePrincipalCreated(sysserviceprincipal);

            var existingItem = Context.SysServicePrincipal
                              .Where(i => i.ServicePrincipalID == sysserviceprincipal.ServicePrincipalID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysServicePrincipal.Add(sysserviceprincipal);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysserviceprincipal).State = EntityState.Detached;
                throw;
            }

            OnAfterSysServicePrincipalCreated(sysserviceprincipal);

            return sysserviceprincipal;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> CancelSysServicePrincipalChanges(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysServicePrincipalUpdated(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item);
        partial void OnAfterSysServicePrincipalUpdated(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> UpdateSysServicePrincipal(int serviceprincipalid, SQLFlowUi.Models.sqlflowProd.SysServicePrincipal sysserviceprincipal)
        {
            OnSysServicePrincipalUpdated(sysserviceprincipal);

            var itemToUpdate = Context.SysServicePrincipal
                              .Where(i => i.ServicePrincipalID == sysserviceprincipal.ServicePrincipalID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysserviceprincipal);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysServicePrincipalUpdated(sysserviceprincipal);

            return sysserviceprincipal;
        }

        partial void OnSysServicePrincipalDeleted(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item);
        partial void OnAfterSysServicePrincipalDeleted(SQLFlowUi.Models.sqlflowProd.SysServicePrincipal item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> DeleteSysServicePrincipal(int serviceprincipalid)
        {
            var itemToDelete = Context.SysServicePrincipal
                              .Where(i => i.ServicePrincipalID == serviceprincipalid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysServicePrincipalDeleted(itemToDelete);


            Context.SysServicePrincipal.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysServicePrincipalDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysSourceControlToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syssourcecontrol/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syssourcecontrol/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysSourceControlToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syssourcecontrol/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syssourcecontrol/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysSourceControlRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysSourceControl> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysSourceControl>> GetSysSourceControl(Query query = null)
        {
            var items = Context.SysSourceControl.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysSourceControlRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysSourceControlGet(SQLFlowUi.Models.sqlflowProd.SysSourceControl item);
        partial void OnGetSysSourceControlBySourceControlId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysSourceControl> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControl> GetSysSourceControlBySourceControlId(int sourcecontrolid)
        {
            var items = Context.SysSourceControl
                              .AsNoTracking()
                              .Where(i => i.SourceControlID == sourcecontrolid);

 
            OnGetSysSourceControlBySourceControlId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysSourceControlGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysSourceControlCreated(SQLFlowUi.Models.sqlflowProd.SysSourceControl item);
        partial void OnAfterSysSourceControlCreated(SQLFlowUi.Models.sqlflowProd.SysSourceControl item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControl> CreateSysSourceControl(SQLFlowUi.Models.sqlflowProd.SysSourceControl syssourcecontrol)
        {
            OnSysSourceControlCreated(syssourcecontrol);

            var existingItem = Context.SysSourceControl
                              .Where(i => i.SourceControlID == syssourcecontrol.SourceControlID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysSourceControl.Add(syssourcecontrol);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syssourcecontrol).State = EntityState.Detached;
                throw;
            }

            OnAfterSysSourceControlCreated(syssourcecontrol);

            return syssourcecontrol;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControl> CancelSysSourceControlChanges(SQLFlowUi.Models.sqlflowProd.SysSourceControl item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysSourceControlUpdated(SQLFlowUi.Models.sqlflowProd.SysSourceControl item);
        partial void OnAfterSysSourceControlUpdated(SQLFlowUi.Models.sqlflowProd.SysSourceControl item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControl> UpdateSysSourceControl(int sourcecontrolid, SQLFlowUi.Models.sqlflowProd.SysSourceControl syssourcecontrol)
        {
            OnSysSourceControlUpdated(syssourcecontrol);

            var itemToUpdate = Context.SysSourceControl
                              .Where(i => i.SourceControlID == syssourcecontrol.SourceControlID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syssourcecontrol);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysSourceControlUpdated(syssourcecontrol);

            return syssourcecontrol;
        }

        partial void OnSysSourceControlDeleted(SQLFlowUi.Models.sqlflowProd.SysSourceControl item);
        partial void OnAfterSysSourceControlDeleted(SQLFlowUi.Models.sqlflowProd.SysSourceControl item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControl> DeleteSysSourceControl(int sourcecontrolid)
        {
            var itemToDelete = Context.SysSourceControl
                              .Where(i => i.SourceControlID == sourcecontrolid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysSourceControlDeleted(itemToDelete);


            Context.SysSourceControl.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysSourceControlDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysSourceControlTypeToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syssourcecontroltype/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syssourcecontroltype/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysSourceControlTypeToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/syssourcecontroltype/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/syssourcecontroltype/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysSourceControlTypeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysSourceControlType>> GetSysSourceControlType(Query query = null)
        {
            var items = Context.SysSourceControlType.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysSourceControlTypeRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysSourceControlTypeGet(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item);
        partial void OnGetSysSourceControlTypeBySourceControlTypeId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> GetSysSourceControlTypeBySourceControlTypeId(int sourcecontroltypeid)
        {
            var items = Context.SysSourceControlType
                              .AsNoTracking()
                              .Where(i => i.SourceControlTypeID == sourcecontroltypeid);

 
            OnGetSysSourceControlTypeBySourceControlTypeId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysSourceControlTypeGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysSourceControlTypeCreated(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item);
        partial void OnAfterSysSourceControlTypeCreated(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> CreateSysSourceControlType(SQLFlowUi.Models.sqlflowProd.SysSourceControlType syssourcecontroltype)
        {
            OnSysSourceControlTypeCreated(syssourcecontroltype);

            var existingItem = Context.SysSourceControlType
                              .Where(i => i.SourceControlTypeID == syssourcecontroltype.SourceControlTypeID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysSourceControlType.Add(syssourcecontroltype);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(syssourcecontroltype).State = EntityState.Detached;
                throw;
            }

            OnAfterSysSourceControlTypeCreated(syssourcecontroltype);

            return syssourcecontroltype;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> CancelSysSourceControlTypeChanges(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysSourceControlTypeUpdated(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item);
        partial void OnAfterSysSourceControlTypeUpdated(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> UpdateSysSourceControlType(int sourcecontroltypeid, SQLFlowUi.Models.sqlflowProd.SysSourceControlType syssourcecontroltype)
        {
            OnSysSourceControlTypeUpdated(syssourcecontroltype);

            var itemToUpdate = Context.SysSourceControlType
                              .Where(i => i.SourceControlTypeID == syssourcecontroltype.SourceControlTypeID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syssourcecontroltype);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysSourceControlTypeUpdated(syssourcecontroltype);

            return syssourcecontroltype;
        }

        partial void OnSysSourceControlTypeDeleted(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item);
        partial void OnAfterSysSourceControlTypeDeleted(SQLFlowUi.Models.sqlflowProd.SysSourceControlType item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> DeleteSysSourceControlType(int sourcecontroltypeid)
        {
            var itemToDelete = Context.SysSourceControlType
                              .Where(i => i.SourceControlTypeID == sourcecontroltypeid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysSourceControlTypeDeleted(itemToDelete);


            Context.SysSourceControlType.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysSourceControlTypeDeleted(itemToDelete);

            return itemToDelete;
        }
    
        public async Task ExportSysStatsToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysstats/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysstats/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportSysStatsToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/sysstats/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/sysstats/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnSysStatsRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysStats> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysStats>> GetSysStats(Query query = null)
        {
            var items = Context.SysStats.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach(var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysStatsRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysStatsGet(SQLFlowUi.Models.sqlflowProd.SysStats item);
        partial void OnGetSysStatsByStatsId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysStats> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysStats> GetSysStatsByStatsId(int statsid)
        {
            var items = Context.SysStats
                              .AsNoTracking()
                              .Where(i => i.StatsID == statsid);

 
            OnGetSysStatsByStatsId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysStatsGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysStatsCreated(SQLFlowUi.Models.sqlflowProd.SysStats item);
        partial void OnAfterSysStatsCreated(SQLFlowUi.Models.sqlflowProd.SysStats item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysStats> CreateSysStats(SQLFlowUi.Models.sqlflowProd.SysStats sysstats)
        {
            OnSysStatsCreated(sysstats);

            var existingItem = Context.SysStats
                              .Where(i => i.StatsID == sysstats.StatsID)
                              .FirstOrDefault();

            if (existingItem != null)
            {
               throw new Exception("Item already available");
            }            

            try
            {
                Context.SysStats.Add(sysstats);
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(sysstats).State = EntityState.Detached;
                throw;
            }

            OnAfterSysStatsCreated(sysstats);

            return sysstats;
        }

        public async Task<SQLFlowUi.Models.sqlflowProd.SysStats> CancelSysStatsChanges(SQLFlowUi.Models.sqlflowProd.SysStats item)
        {
            var entityToCancel = Context.Entry(item);
            if (entityToCancel.State == EntityState.Modified)
            {
              entityToCancel.CurrentValues.SetValues(entityToCancel.OriginalValues);
              entityToCancel.State = EntityState.Unchanged;
            }

            return item;
        }

        partial void OnSysStatsUpdated(SQLFlowUi.Models.sqlflowProd.SysStats item);
        partial void OnAfterSysStatsUpdated(SQLFlowUi.Models.sqlflowProd.SysStats item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysStats> UpdateSysStats(int statsid, SQLFlowUi.Models.sqlflowProd.SysStats sysstats)
        {
            OnSysStatsUpdated(sysstats);

            var itemToUpdate = Context.SysStats
                              .Where(i => i.StatsID == sysstats.StatsID)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
               throw new Exception("Item no longer available");
            }
                
            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(sysstats);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysStatsUpdated(sysstats);

            return sysstats;
        }

        partial void OnSysStatsDeleted(SQLFlowUi.Models.sqlflowProd.SysStats item);
        partial void OnAfterSysStatsDeleted(SQLFlowUi.Models.sqlflowProd.SysStats item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysStats> DeleteSysStats(int statsid)
        {
            var itemToDelete = Context.SysStats
                              .Where(i => i.StatsID == statsid)
                              .FirstOrDefault();

            if (itemToDelete == null)
            {
               throw new Exception("Item no longer available");
            }

            OnSysStatsDeleted(itemToDelete);


            Context.SysStats.Remove(itemToDelete);

            try
            {
                Context.SaveChanges();
            }
            catch
            {
                Context.Entry(itemToDelete).State = EntityState.Unchanged;
                throw;
            }

            OnAfterSysStatsDeleted(itemToDelete);

            return itemToDelete;
        }

        //CustomCode Bellow This:
        partial void OnSysDocSubSetRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocSubSet> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDocSubSet>> GetSysDocSubSet(Query query = null)
        {
            var items = Context.SysDocSubset.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDocSubSetRead(ref items);

            return await Task.FromResult(items);
        }


        public async Task<SQLFlowUi.Models.sqlflowProd.SysDoc> GetSysDocByObjectname(string ObjectName)
        {
            var items = Context.SysDoc
                .AsNoTracking()
                .Where(i => i.ObjectName == ObjectName);


            OnGetSysDocBySysDocId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysDocGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }



        public async Task ExportReportFlowHealthCheckToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/dwsqlflowprod/reportflowhealthcheck/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/dwsqlflowprod/reportflowhealthcheck1/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportReportFlowHealthCheckToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/dwsqlflowprod/reportflowhealthcheck/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/dwsqlflowprod/reportflowhealthcheck1/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnReportFlowHealthCheckRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck>> GetReportFlowHealthCheck(Query query = null)
        {
            var items = Context.ReportFlowHealthCheck.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnReportFlowHealthCheckRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnFlowDsRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS>> GetFlowDs(Query query = null)
        {
            var items = Context.FlowDs.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnFlowDsRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysOpenAIModelsRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysOpenAIModel> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysOpenAIModel>> GetSysOpenAIModels(Query query = null)
        {
            var items = Context.SysOpenAIModel.AsQueryable();

            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysOpenAIModelsRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnReportAssertionRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.ReportAssertion> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.ReportAssertion>> GetReportAssertion(Query query = null)
        {
            var items = Context.ReportAssertion.AsQueryable();

            //Context.ReportAssertion.r
            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnReportAssertionRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysFlowNoteTypeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowNoteType> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowNoteType>> GetSysFlowNoteTypes(Query query = null)
        {
            var items = Context.SysFlowNoteType.AsQueryable();

            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysFlowNoteTypeRead(ref items);

            return await Task.FromResult(items);
        }


        partial void OnSysExportByRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysExportBy> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysExportBy>> GetSysExportBy(Query query = null)
        {
            var items = Context.SysExportBy.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysExportByRead(ref items);

            return await Task.FromResult(items);
        }


        partial void OnSysSubFolderPatternsRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysSubFolderPattern> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysSubFolderPattern>> GetSysSubFolderPatterns(Query query = null)
        {
            var items = Context.SysSubFolderPatterns.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysSubFolderPatternsRead(ref items);

            return await Task.FromResult(items);
        }


        partial void OnSysCompressionTypeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysCompressionType> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysCompressionType>> GetSysCompressionType(Query query = null)
        {
            var items = Context.SysCompressionType.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysCompressionTypeRead(ref items);

            return await Task.FromResult(items);
        }


        partial void OnSysFileEncodingsRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysFileEncoding> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysFileEncoding>> GetSysFileEncodings(Query query = null)
        {
            var items = Context.SysFileEncodings.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysFileEncodingsRead(ref items);

            return await Task.FromResult(items);
        }


        partial void OnSysHashKeyTypeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysHashKeyType> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysHashKeyType>> GetSysHashKeyType(Query query = null)
        {
            var items = Context.SysHashKeyType.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysHashKeyTypeRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnDataSubscriberTypeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSubscriberType> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSubscriberType>> GetDataSubscriberType(Query query = null)
        {
            var items = Context.DataSubscriberType.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnDataSubscriberTypeRead(ref items);

            return await Task.FromResult(items);
        }



        partial void OnDataSubscriberTypeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysMatchKeyDeletedRowHandeling> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysMatchKeyDeletedRowHandeling>> GetSysMatchKeyDeletedRowHandeling(Query query = null)
        {
            var items = Context.SysMatchKeyDeletedRowHandeling.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnDataSubscriberTypeRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnSysFlowTypeRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowType> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowType>> GetSysFlowType(Query query = null)
        {
            var items = Context.SysFlowType.AsQueryable();

            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysFlowTypeRead(ref items);

            return await Task.FromResult(items);
        }


        public async Task<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> GetSysServicePrincipalByAlias(string servicePrincipalAlias)
        {
            var items = Context.SysServicePrincipal
                .AsNoTracking()
                .Where(i => i.ServicePrincipalAlias == servicePrincipalAlias);


            OnGetSysServicePrincipalByServicePrincipalId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysServicePrincipalGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.GetApiKey>> GetGetGoogleApiKeys(Query query = null)
        {
            OnGetGoogleApiKeysDefaultParams();

            var items = Context.GetGoogleApiKeys.FromSqlInterpolated($"EXEC [flw].[GetApiKey] ").ToList().AsQueryable();

            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnGetGoogleApiKeysInvoke(ref items);

            return await Task.FromResult(items);
        }

        partial void OnGetGoogleApiKeysDefaultParams();

        partial void OnGetGoogleApiKeysInvoke(ref IQueryable<SQLFlowUi.Models.sqlflowProd.GetApiKey> items);


        partial void OnReportBatchStartEndGet(SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd item);
        partial void OnGetReportBatchStartEndByFlowId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd> GetReportBatchStartEndByFlowId(int flowid)
        {
            var items = Context.ReportBatchStartEnd
                              .AsNoTracking()
                              .Where(i => i.FlowID == flowid);


            OnGetReportBatchStartEndByFlowId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnReportBatchStartEndGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnReportBatchStartEndCreated(SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd item);
        partial void OnAfterReportBatchStartEndCreated(SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd item);

        partial void OnReportBatchStartEndRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd> items);

        public async Task ExportReportAssertionToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/reportassertion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/reportassertion/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportReportAssertionToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/reportassertion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/reportassertion/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportReportBatchStartEndToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/reportbatchstartend/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/reportbatchstartend/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportReportBatchStartEndToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/reportbatchstartend/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/reportbatchstartend/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd>> GetReportBatchStartEnd(Query query = null)
        {
            var items = Context.ReportBatchStartEnd.AsQueryable();


            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnReportBatchStartEndRead(ref items);

            return await Task.FromResult(items);
        }

        partial void OnGetSysLogFileByRecId(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysLogFile> items);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysLogFile> GetSysLogFileByRecId(int recid)
        {
            var items = Context.SysLogFile
                              .AsNoTracking()
                              .Where(i => i.LogFileID == recid);


            OnGetSysLogFileByRecId(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysLogFileGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        public async Task ExportFlowDsToCSV(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/flowds/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/flowds/csv(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        public async Task ExportFlowDsToExcel(Query query = null, string fileName = null)
        {
            navigationManager.NavigateTo(query != null ? query.ToUrl($"export/sqlflowprod/flowds/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')") : $"export/sqlflowprod/flowds/excel(fileName='{(!string.IsNullOrEmpty(fileName) ? UrlEncoder.Default.Encode(fileName) : "Export")}')", true);
        }

        partial void OnGetDataSubscriberQueryByQueryID(ref IQueryable<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> items);

        public async Task<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> GetDataSubscriberQueryByQueryID(int QueryID)
        {
            var items = Context.DataSubscriberQuery
                              .AsNoTracking()
                              .Where(i => i.QueryID == QueryID);

            items = items.Include(i => i.DataSubscriber);

            OnGetDataSubscriberQueryByQueryID(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnDataSubscriberQueryGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }

        partial void OnSysHashKeyTypeGet(SQLFlowUi.Models.sqlflowProd.SysHashKeyType item);
        partial void OnGetSysHashKeyTypeByHashKeyType(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysHashKeyType> items);


        public async Task<SQLFlowUi.Models.sqlflowProd.SysHashKeyType> GetSysHashKeyTypeByHashKeyType(string hashkeytype)
        {
            var items = Context.SysHashKeyType
                              .AsNoTracking()
                              .Where(i => i.HashKeyType == hashkeytype);


            OnGetSysHashKeyTypeByHashKeyType(ref items);

            var itemToReturn = items.FirstOrDefault();

            OnSysHashKeyTypeGet(itemToReturn);

            return await Task.FromResult(itemToReturn);
        }



        partial void OnSysDetectUniqueKeyRead(ref IQueryable<SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey> items);

        public async Task<IQueryable<SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey>> GetSysDetectUniqueKey(Query query = null)
        {
            var items = Context.SysDetectUniqueKey.AsQueryable();

            if (query != null)
            {
                if (!string.IsNullOrEmpty(query.Expand))
                {
                    var propertiesToExpand = query.Expand.Split(',');
                    foreach (var p in propertiesToExpand)
                    {
                        items = items.Include(p.Trim());
                    }
                }

                ApplyQuery(ref items, query);
            }

            OnSysDetectUniqueKeyRead(ref items);

            return await Task.FromResult(items);
        }


        partial void OnSysHashKeyTypeUpdated(SQLFlowUi.Models.sqlflowProd.SysHashKeyType item);
        partial void OnAfterSysHashKeyTypeUpdated(SQLFlowUi.Models.sqlflowProd.SysHashKeyType item);

        public async Task<SQLFlowUi.Models.sqlflowProd.SysHashKeyType> UpdateSysHashKeyType(string hashkeytype, SQLFlowUi.Models.sqlflowProd.SysHashKeyType syshashkeytype)
        {
            OnSysHashKeyTypeUpdated(syshashkeytype);

            var itemToUpdate = Context.SysHashKeyType
                              .Where(i => i.HashKeyType == syshashkeytype.HashKeyType)
                              .FirstOrDefault();

            if (itemToUpdate == null)
            {
                throw new Exception("Item no longer available");
            }

            var entryToUpdate = Context.Entry(itemToUpdate);
            entryToUpdate.CurrentValues.SetValues(syshashkeytype);
            entryToUpdate.State = EntityState.Modified;

            Context.SaveChanges();

            OnAfterSysHashKeyTypeUpdated(syshashkeytype);

            return syshashkeytype;
        }
    }
}