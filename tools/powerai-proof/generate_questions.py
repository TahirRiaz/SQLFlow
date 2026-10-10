# -*- coding: utf-8 -*-
"""Builds the PowerAI proof-value question set: a natural-language question paired with the SQL that
answers it. Every column named here was verified against the live AdventureWorksDW sample."""
import json

Q = []
n = 0
def q(shape, question, sql, tags):
    global n
    n += 1
    Q.append({"id": f"q{n:03d}", "shape": shape, "question": question,
              "sql": " ".join(sql.split()), "tags": tags})

F, P, R, T, D, C = ("dbo.FactResellerSales", "dbo.DimProduct", "dbo.DimReseller",
                    "dbo.DimSalesTerritory", "dbo.DimDate", "dbo.DimCustomer")
JT = f"JOIN {T} t ON t.SalesTerritoryKey = f.SalesTerritoryKey"
JP = f"JOIN {P} p ON p.ProductKey = f.ProductKey"
JR = f"JOIN {R} r ON r.ResellerKey = f.ResellerKey"
JD = f"JOIN {D} d ON d.DateKey = f.OrderDateKey"

# ============================================================ SCALAR ============================================
for tbl, label, name in [(F,"reseller sales line items","factresellersales"),(P,"products","dimproduct"),
                         (R,"resellers","dimreseller"),(T,"sales territories","dimsalesterritory"),
                         (D,"dates","dimdate"),(C,"customers","dimcustomer")]:
    q("scalar", f"How many {label} are there?", f"SELECT COUNT(*) AS Value FROM {tbl}", ["count", name])

for col, label in [("SalesAmount","total sales amount"),("OrderQuantity","total order quantity"),
                   ("TaxAmt","total tax"),("Freight","total freight"),("TotalProductCost","total product cost"),
                   ("DiscountAmount","total discount"),("ExtendedAmount","total extended amount")]:
    q("scalar", f"What is the {label} across all reseller sales?",
      f"SELECT SUM(f.{col}) AS Value FROM {F} f", ["sum","factresellersales",col.lower()])

for col, label in [("SalesAmount","average sale amount"),("UnitPrice","average unit price"),
                   ("OrderQuantity","average order quantity"),("ProductStandardCost","average product standard cost")]:
    q("scalar", f"What is the {label} on reseller sales?",
      f"SELECT AVG(f.{col}) AS Value FROM {F} f", ["avg","factresellersales",col.lower()])

for agg, word in [("MIN","smallest"),("MAX","largest")]:
    for col, label in [("SalesAmount","sale amount"),("UnitPrice","unit price"),("OrderQuantity","order quantity")]:
        q("scalar", f"What is the {word} {label} on any reseller sales line?",
          f"SELECT {agg}(f.{col}) AS Value FROM {F} f", [agg.lower(),"factresellersales",col.lower()])

for tbl, col, label, name in [(F,"SalesOrderNumber","distinct sales orders","factresellersales"),
        (F,"ProductKey","distinct products sold","factresellersales"),
        (F,"ResellerKey","distinct resellers with sales","factresellersales"),
        (F,"SalesTerritoryKey","distinct territories with sales","factresellersales"),
        (F,"EmployeeKey","distinct employees with sales","factresellersales"),
        (F,"CurrencyKey","distinct currencies used","factresellersales"),
        (F,"PromotionKey","distinct promotions applied","factresellersales"),
        (P,"Color","distinct product colors","dimproduct"),(P,"ModelName","distinct product models","dimproduct"),
        (P,"ProductLine","distinct product lines","dimproduct"),(P,"Class","distinct product classes","dimproduct"),
        (P,"Style","distinct product styles","dimproduct"),(P,"Size","distinct product sizes","dimproduct"),
        (R,"BusinessType","distinct reseller business types","dimreseller"),
        (R,"ProductLine","distinct reseller product lines","dimreseller"),
        (R,"BankName","distinct reseller banks","dimreseller"),
        (T,"SalesTerritoryCountry","distinct countries","dimsalesterritory"),
        (T,"SalesTerritoryRegion","distinct regions","dimsalesterritory"),
        (T,"SalesTerritoryGroup","distinct territory groups","dimsalesterritory"),
        (D,"CalendarYear","distinct calendar years","dimdate"),
        (C,"LastName","distinct customer last names","dimcustomer"),
        (C,"EnglishOccupation","distinct customer occupations","dimcustomer")]:
    q("scalar", f"How many {label} are there?",
      f"SELECT COUNT(DISTINCT {col}) AS Value FROM {tbl}", ["distinct",name,col.lower()])

for yr in [2010,2011,2012,2013]:
    q("scalar", f"What were total reseller sales in {yr}?",
      f"SELECT SUM(f.SalesAmount) AS Value FROM {F} f WHERE YEAR(f.OrderDate) = {yr}", ["sum","year",str(yr)])
    q("scalar", f"How many reseller sales orders were placed in {yr}?",
      f"SELECT COUNT(DISTINCT f.SalesOrderNumber) AS Value FROM {F} f WHERE YEAR(f.OrderDate) = {yr}", ["count","year",str(yr)])
    q("scalar", f"How many units were sold to resellers in {yr}?",
      f"SELECT SUM(f.OrderQuantity) AS Value FROM {F} f WHERE YEAR(f.OrderDate) = {yr}", ["sum","year",str(yr)])

for country in ["United States","Canada","France","Germany","United Kingdom","Australia"]:
    q("scalar", f"What were total reseller sales in {country}?",
      f"SELECT SUM(f.SalesAmount) AS Value FROM {F} f {JT} WHERE t.SalesTerritoryCountry = '{country}'", ["sum","country"])
for grp in ["Europe","North America","Pacific"]:
    q("scalar", f"What were total reseller sales in the {grp} territory group?",
      f"SELECT SUM(f.SalesAmount) AS Value FROM {F} f {JT} WHERE t.SalesTerritoryGroup = '{grp}'", ["sum","group"])
for bt in ["Specialty Bike Shop","Value Added Reseller","Warehouse"]:
    q("scalar", f"What were total sales to {bt} resellers?",
      f"SELECT SUM(f.SalesAmount) AS Value FROM {F} f {JR} WHERE r.BusinessType = '{bt}'", ["sum","biztype"])
    q("scalar", f"How many {bt} resellers are there?",
      f"SELECT COUNT(*) AS Value FROM {R} WHERE BusinessType = '{bt}'", ["count","biztype"])
for color in ["Black","Red","Silver","Blue","Yellow","White","Multi","Grey"]:
    q("scalar", f"How many products are {color}?",
      f"SELECT COUNT(*) AS Value FROM {P} WHERE Color = '{color}'", ["count","color"])
    q("scalar", f"What were total reseller sales of {color} products?",
      f"SELECT SUM(f.SalesAmount) AS Value FROM {F} f {JP} WHERE p.Color = '{color}'", ["sum","color"])

# a few more scalars: ratios, date bounds, guarded division
q("scalar","What is the total profit on reseller sales?",
  f"SELECT SUM(f.SalesAmount) - SUM(f.TotalProductCost) AS Value FROM {F} f",["profit"])
q("scalar","What is the overall gross margin percentage on reseller sales?",
  f"SELECT CASE WHEN SUM(f.SalesAmount) = 0 THEN NULL ELSE (SUM(f.SalesAmount) - SUM(f.TotalProductCost)) * 100.0 / SUM(f.SalesAmount) END AS Value FROM {F} f",["margin"])
q("scalar","What is the average number of lines per reseller sales order?",
  f"SELECT CAST(COUNT(*) AS float) / NULLIF(COUNT(DISTINCT f.SalesOrderNumber), 0) AS Value FROM {F} f",["avg"])
q("scalar","What is the earliest reseller sales order date?", f"SELECT MIN(f.OrderDate) AS Value FROM {F} f",["min","date"])
q("scalar","What is the latest reseller sales order date?", f"SELECT MAX(f.OrderDate) AS Value FROM {F} f",["max","date"])
q("scalar","What is the highest list price of any product?", f"SELECT MAX(ListPrice) AS Value FROM {P}",["max","dimproduct"])
q("scalar","What is the average list price of products that have one?",
  f"SELECT AVG(ListPrice) AS Value FROM {P} WHERE ListPrice IS NOT NULL",["avg","dimproduct"])
q("scalar","How many products have no list price recorded?",
  f"SELECT COUNT(*) AS Value FROM {P} WHERE ListPrice IS NULL",["null","dimproduct"])
q("scalar","How many products are finished goods?",
  f"SELECT COUNT(*) AS Value FROM {P} WHERE FinishedGoodsFlag = 1",["count","dimproduct"])
q("scalar","What is the total annual sales reported across all resellers?",
  f"SELECT SUM(AnnualSales) AS Value FROM {R}",["sum","dimreseller"])
q("scalar","What is the average number of employees at a reseller?",
  f"SELECT AVG(CAST(NumberEmployees AS float)) AS Value FROM {R}",["avg","dimreseller"])
q("scalar","How many resellers have never recorded a sale?",
  f"SELECT COUNT(*) AS Value FROM {R} r WHERE NOT EXISTS (SELECT 1 FROM {F} f WHERE f.ResellerKey = r.ResellerKey)",["antijoin"])
q("scalar","How many products have never been sold to a reseller?",
  f"SELECT COUNT(*) AS Value FROM {P} p WHERE NOT EXISTS (SELECT 1 FROM {F} f WHERE f.ProductKey = p.ProductKey)",["antijoin"])
q("scalar","What is the average yearly income of customers?",
  f"SELECT AVG(YearlyIncome) AS Value FROM {C}",["avg","dimcustomer"])
q("scalar","How many customers own a house?",
  f"SELECT COUNT(*) AS Value FROM {C} WHERE HouseOwnerFlag = '1'",["count","dimcustomer"])
q("scalar","What is the largest single reseller sales order by total amount?",
  f"SELECT MAX(v.Total) AS Value FROM (SELECT f.SalesOrderNumber, SUM(f.SalesAmount) AS Total FROM {F} f GROUP BY f.SalesOrderNumber) v",["max","subquery"])
q("scalar","How many reseller sales lines had a discount applied?",
  f"SELECT COUNT(*) AS Value FROM {F} f WHERE f.DiscountAmount > 0",["count","discount"])
q("scalar","What share of reseller sales lines carried a carrier tracking number?",
  f"SELECT COUNT(f.CarrierTrackingNumber) * 100.0 / NULLIF(COUNT(*), 0) AS Value FROM {F} f",["ratio","null"])


# ============================================================ MULTIVALUE (one row, several columns) ============
q("multivalue","Give me a summary of reseller sales: order count, units, and total amount.",
  f"SELECT COUNT(DISTINCT f.SalesOrderNumber) AS Orders, SUM(f.OrderQuantity) AS Units, SUM(f.SalesAmount) AS Sales FROM {F} f",["summary"])
q("multivalue","What are the minimum, maximum, and average sale amounts on reseller sales?",
  f"SELECT MIN(f.SalesAmount) AS MinSales, MAX(f.SalesAmount) AS MaxSales, AVG(f.SalesAmount) AS AvgSales FROM {F} f",["spread"])
q("multivalue","Show total sales, cost, and profit for reseller sales.",
  f"SELECT SUM(f.SalesAmount) AS Sales, SUM(f.TotalProductCost) AS Cost, SUM(f.SalesAmount) - SUM(f.TotalProductCost) AS Profit FROM {F} f",["profit"])
q("multivalue","What is the date range of reseller sales orders?",
  f"SELECT MIN(f.OrderDate) AS FirstOrder, MAX(f.OrderDate) AS LastOrder FROM {F} f",["range","date"])
q("multivalue","How many products, resellers, and territories does the warehouse hold?",
  f"SELECT (SELECT COUNT(*) FROM {P}) AS Products, (SELECT COUNT(*) FROM {R}) AS Resellers, (SELECT COUNT(*) FROM {T}) AS Territories",["counts"])
q("multivalue","Break down reseller sales into tax, freight, and net amount.",
  f"SELECT SUM(f.TaxAmt) AS Tax, SUM(f.Freight) AS Freight, SUM(f.SalesAmount) AS Sales FROM {F} f",["breakdown"])
q("multivalue","Summarise the product list price range.",
  f"SELECT MIN(ListPrice) AS MinPrice, MAX(ListPrice) AS MaxPrice, AVG(ListPrice) AS AvgPrice FROM {P} WHERE ListPrice IS NOT NULL",["spread","dimproduct"])
q("multivalue","How many distinct products, orders, and resellers appear in the sales fact?",
  f"SELECT COUNT(DISTINCT f.ProductKey) AS Products, COUNT(DISTINCT f.SalesOrderNumber) AS Orders, COUNT(DISTINCT f.ResellerKey) AS Resellers FROM {F} f",["distinct"])
q("multivalue","What were sales, units, and average unit price in 2013?",
  f"SELECT SUM(f.SalesAmount) AS Sales, SUM(f.OrderQuantity) AS Units, AVG(f.UnitPrice) AS AvgUnitPrice FROM {F} f WHERE YEAR(f.OrderDate) = 2013",["year","2013"])
q("multivalue","Compare total reseller sales between 2012 and 2013.",
  f"SELECT SUM(CASE WHEN YEAR(f.OrderDate) = 2012 THEN f.SalesAmount ELSE 0 END) AS Sales2012, SUM(CASE WHEN YEAR(f.OrderDate) = 2013 THEN f.SalesAmount ELSE 0 END) AS Sales2013 FROM {F} f",["compare","year"])
q("multivalue","Show the reseller count for each business type in one row.",
  f"SELECT SUM(CASE WHEN BusinessType = 'Warehouse' THEN 1 ELSE 0 END) AS Warehouse, SUM(CASE WHEN BusinessType = 'Value Added Reseller' THEN 1 ELSE 0 END) AS ValueAdded, SUM(CASE WHEN BusinessType = 'Specialty Bike Shop' THEN 1 ELSE 0 END) AS SpecialtyBikeShop FROM {R}",["pivot","biztype"])
q("multivalue","What is the split of reseller sales between North America, Europe, and Pacific?",
  f"SELECT SUM(CASE WHEN t.SalesTerritoryGroup = 'North America' THEN f.SalesAmount ELSE 0 END) AS NorthAmerica, SUM(CASE WHEN t.SalesTerritoryGroup = 'Europe' THEN f.SalesAmount ELSE 0 END) AS Europe, SUM(CASE WHEN t.SalesTerritoryGroup = 'Pacific' THEN f.SalesAmount ELSE 0 END) AS Pacific FROM {F} f {JT}",["pivot","group"])
q("multivalue","Summarise customer demographics: total, house owners, and average income.",
  f"SELECT COUNT(*) AS Customers, SUM(CASE WHEN HouseOwnerFlag = '1' THEN 1 ELSE 0 END) AS HouseOwners, AVG(YearlyIncome) AS AvgIncome FROM {C}",["dimcustomer"])
q("multivalue","What is the highest and lowest total on a single reseller sales order?",
  f"SELECT MAX(v.Total) AS MaxOrder, MIN(v.Total) AS MinOrder FROM (SELECT f.SalesOrderNumber, SUM(f.SalesAmount) AS Total FROM {F} f GROUP BY f.SalesOrderNumber) v",["subquery"])
q("multivalue","Give the average order quantity, discount, and unit price discount percentage.",
  f"SELECT AVG(CAST(f.OrderQuantity AS float)) AS AvgQty, AVG(f.DiscountAmount) AS AvgDiscount, AVG(f.UnitPriceDiscountPct) AS AvgDiscountPct FROM {F} f",["avg"])
q("multivalue","How many products are finished goods versus not?",
  f"SELECT SUM(CASE WHEN FinishedGoodsFlag = 1 THEN 1 ELSE 0 END) AS Finished, SUM(CASE WHEN FinishedGoodsFlag = 0 THEN 1 ELSE 0 END) AS NotFinished FROM {P}",["pivot","dimproduct"])
q("multivalue","What is the calendar year range and total days in the date dimension?",
  f"SELECT MIN(CalendarYear) AS FirstYear, MAX(CalendarYear) AS LastYear, COUNT(*) AS Days FROM {D}",["range","dimdate"])
q("multivalue","Summarise reseller annual sales and employee counts.",
  f"SELECT SUM(AnnualSales) AS TotalAnnualSales, AVG(CAST(NumberEmployees AS float)) AS AvgEmployees, MAX(NumberEmployees) AS MaxEmployees FROM {R}",["dimreseller"])
q("multivalue","What proportion of reseller sales lines were discounted?",
  f"SELECT COUNT(*) AS TotalLines, SUM(CASE WHEN f.DiscountAmount > 0 THEN 1 ELSE 0 END) AS DiscountedLines, SUM(CASE WHEN f.DiscountAmount > 0 THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS PctDiscounted FROM {F} f",["ratio"])
q("multivalue","Give a one-line profitability summary for the United States.",
  f"SELECT SUM(f.SalesAmount) AS Sales, SUM(f.TotalProductCost) AS Cost, SUM(f.SalesAmount) - SUM(f.TotalProductCost) AS Profit FROM {F} f {JT} WHERE t.SalesTerritoryCountry = 'United States'",["profit","country"])

# ============================================================ DATASET (many rows) ==============================
q("dataset","What are reseller sales by territory region?",
  f"SELECT t.SalesTerritoryRegion, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} GROUP BY t.SalesTerritoryRegion ORDER BY t.SalesTerritoryRegion",["groupby","region"])
q("dataset","What are reseller sales by country?",
  f"SELECT t.SalesTerritoryCountry, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["groupby","country"])
q("dataset","What are reseller sales by territory group?",
  f"SELECT t.SalesTerritoryGroup, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} GROUP BY t.SalesTerritoryGroup ORDER BY t.SalesTerritoryGroup",["groupby","group"])
q("dataset","What are reseller sales by business type?",
  f"SELECT r.BusinessType, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} GROUP BY r.BusinessType ORDER BY r.BusinessType",["groupby","biztype"])
q("dataset","What are reseller sales by calendar year?",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["groupby","year"])
q("dataset","What are reseller sales by year and quarter?",
  f"SELECT d.CalendarYear, d.CalendarQuarter, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JD} GROUP BY d.CalendarYear, d.CalendarQuarter ORDER BY d.CalendarYear, d.CalendarQuarter",["groupby","quarter"])
q("dataset","What are reseller sales by month across all years?",
  f"SELECT d.MonthNumberOfYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JD} GROUP BY d.MonthNumberOfYear ORDER BY d.MonthNumberOfYear",["groupby","month"])
q("dataset","What are reseller sales by product color?",
  f"SELECT p.Color, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} GROUP BY p.Color ORDER BY p.Color",["groupby","color"])
q("dataset","What are reseller sales by product line?",
  f"SELECT p.ProductLine, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} GROUP BY p.ProductLine ORDER BY p.ProductLine",["groupby","prodline"])
q("dataset","What are reseller sales by product class?",
  f"SELECT p.Class, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} GROUP BY p.Class ORDER BY p.Class",["groupby","class"])
q("dataset","How many products are there in each colour?",
  f"SELECT Color, COUNT(*) AS Products FROM {P} GROUP BY Color ORDER BY Color",["groupby","dimproduct"])
q("dataset","How many resellers are in each business type?",
  f"SELECT BusinessType, COUNT(*) AS Resellers FROM {R} GROUP BY BusinessType ORDER BY BusinessType",["groupby","dimreseller"])
q("dataset","How many territories are in each group?",
  f"SELECT SalesTerritoryGroup, COUNT(*) AS Territories FROM {T} GROUP BY SalesTerritoryGroup ORDER BY SalesTerritoryGroup",["groupby","dimsalesterritory"])
q("dataset","List the sales territories with their country and group.",
  f"SELECT SalesTerritoryRegion, SalesTerritoryCountry, SalesTerritoryGroup FROM {T} ORDER BY SalesTerritoryKey",["list","dimsalesterritory"])
q("dataset","What are the top 10 products by reseller sales?",
  f"SELECT TOP 10 p.EnglishProductName, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} GROUP BY p.EnglishProductName ORDER BY SUM(f.SalesAmount) DESC, p.EnglishProductName",["top","product"])
q("dataset","What are the top 10 resellers by sales?",
  f"SELECT TOP 10 r.ResellerName, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} GROUP BY r.ResellerName ORDER BY SUM(f.SalesAmount) DESC, r.ResellerName",["top","reseller"])
q("dataset","What are the top 10 product models by units sold?",
  f"SELECT TOP 10 p.ModelName, SUM(f.OrderQuantity) AS Units FROM {F} f {JP} WHERE p.ModelName IS NOT NULL GROUP BY p.ModelName ORDER BY SUM(f.OrderQuantity) DESC, p.ModelName",["top","model"])
q("dataset","Which 10 products have the highest list price?",
  f"SELECT TOP 10 EnglishProductName, ListPrice FROM {P} WHERE ListPrice IS NOT NULL ORDER BY ListPrice DESC, EnglishProductName",["top","dimproduct"])
q("dataset","Show the five largest reseller sales orders by total amount.",
  f"SELECT TOP 5 f.SalesOrderNumber, SUM(f.SalesAmount) AS OrderTotal FROM {F} f GROUP BY f.SalesOrderNumber ORDER BY SUM(f.SalesAmount) DESC, f.SalesOrderNumber",["top","order"])
q("dataset","Show sales by country and year.",
  f"SELECT t.SalesTerritoryCountry, YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry, YEAR(f.OrderDate) ORDER BY t.SalesTerritoryCountry, OrderYear",["groupby","crosstab"])
q("dataset","Show sales by business type and year.",
  f"SELECT r.BusinessType, YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} GROUP BY r.BusinessType, YEAR(f.OrderDate) ORDER BY r.BusinessType, OrderYear",["groupby","crosstab"])
q("dataset","Show units sold by reseller country.",
  f"SELECT t.SalesTerritoryCountry, SUM(f.OrderQuantity) AS Units FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["groupby","units"])
q("dataset","Show profit by territory group.",
  f"SELECT t.SalesTerritoryGroup, SUM(f.SalesAmount) - SUM(f.TotalProductCost) AS Profit FROM {F} f {JT} GROUP BY t.SalesTerritoryGroup ORDER BY t.SalesTerritoryGroup",["groupby","profit"])
q("dataset","Show the number of orders per year.",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, COUNT(DISTINCT f.SalesOrderNumber) AS Orders FROM {F} f GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["groupby","orders"])
q("dataset","How many customers are in each occupation?",
  f"SELECT EnglishOccupation, COUNT(*) AS Customers FROM {C} GROUP BY EnglishOccupation ORDER BY EnglishOccupation",["groupby","dimcustomer"])
q("dataset","How many customers are in each education level?",
  f"SELECT EnglishEducation, COUNT(*) AS Customers FROM {C} GROUP BY EnglishEducation ORDER BY EnglishEducation",["groupby","dimcustomer"])
q("dataset","What is the gender split of customers?",
  f"SELECT Gender, COUNT(*) AS Customers FROM {C} GROUP BY Gender ORDER BY Gender",["groupby","dimcustomer"])
q("dataset","How many customers fall into each marital status?",
  f"SELECT MaritalStatus, COUNT(*) AS Customers FROM {C} GROUP BY MaritalStatus ORDER BY MaritalStatus",["groupby","dimcustomer"])
q("dataset","How many customers are in each commute distance band?",
  f"SELECT CommuteDistance, COUNT(*) AS Customers FROM {C} GROUP BY CommuteDistance ORDER BY CommuteDistance",["groupby","dimcustomer"])
q("dataset","Show average list price by product line.",
  f"SELECT ProductLine, AVG(ListPrice) AS AvgListPrice FROM {P} WHERE ListPrice IS NOT NULL GROUP BY ProductLine ORDER BY ProductLine",["groupby","dimproduct"])
q("dataset","Show the average unit price by product class.",
  f"SELECT p.Class, AVG(f.UnitPrice) AS AvgUnitPrice FROM {F} f {JP} GROUP BY p.Class ORDER BY p.Class",["groupby","class"])
q("dataset","List resellers with more than 75 employees.",
  f"SELECT ResellerName, NumberEmployees FROM {R} WHERE NumberEmployees > 75 ORDER BY NumberEmployees DESC, ResellerName",["filter","dimreseller"])
q("dataset","Which product subcategories have the most products?",
  f"SELECT TOP 10 ProductSubcategoryKey, COUNT(*) AS Products FROM {P} WHERE ProductSubcategoryKey IS NOT NULL GROUP BY ProductSubcategoryKey ORDER BY COUNT(*) DESC, ProductSubcategoryKey",["top","dimproduct"])
q("dataset","Show reseller sales by day of week.",
  f"SELECT d.EnglishDayNameOfWeek, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JD} GROUP BY d.EnglishDayNameOfWeek ORDER BY d.EnglishDayNameOfWeek",["groupby","dayofweek"])
q("dataset","Show reseller sales by English month name.",
  f"SELECT d.EnglishMonthName, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JD} GROUP BY d.EnglishMonthName ORDER BY d.EnglishMonthName",["groupby","month"])
q("dataset","Show the number of resellers by first order year.",
  f"SELECT FirstOrderYear, COUNT(*) AS Resellers FROM {R} WHERE FirstOrderYear IS NOT NULL GROUP BY FirstOrderYear ORDER BY FirstOrderYear",["groupby","dimreseller"])
q("dataset","Show total sales for each year and territory group.",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, t.SalesTerritoryGroup, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} GROUP BY YEAR(f.OrderDate), t.SalesTerritoryGroup ORDER BY OrderYear, t.SalesTerritoryGroup",["groupby","crosstab"])
q("dataset","Which products were never sold to a reseller?",
  f"SELECT TOP 20 p.EnglishProductName FROM {P} p WHERE NOT EXISTS (SELECT 1 FROM {F} f WHERE f.ProductKey = p.ProductKey) ORDER BY p.EnglishProductName",["antijoin"])
q("dataset","Show the ten cheapest products that have a list price.",
  f"SELECT TOP 10 EnglishProductName, ListPrice FROM {P} WHERE ListPrice IS NOT NULL ORDER BY ListPrice ASC, EnglishProductName",["top","dimproduct"])
q("dataset","Show average order quantity by territory region.",
  f"SELECT t.SalesTerritoryRegion, AVG(CAST(f.OrderQuantity AS float)) AS AvgQty FROM {F} f {JT} GROUP BY t.SalesTerritoryRegion ORDER BY t.SalesTerritoryRegion",["groupby","region"])

# ---- more datasets: per-year slices, per-country slices, richer groupings -------------------------------------
for yr in [2010,2011,2012,2013]:
    q("dataset", f"What were reseller sales by country in {yr}?",
      f"SELECT t.SalesTerritoryCountry, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} WHERE YEAR(f.OrderDate) = {yr} GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["groupby","country",str(yr)])
    q("dataset", f"What were reseller sales by business type in {yr}?",
      f"SELECT r.BusinessType, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} WHERE YEAR(f.OrderDate) = {yr} GROUP BY r.BusinessType ORDER BY r.BusinessType",["groupby","biztype",str(yr)])
    q("dataset", f"Show monthly reseller sales for {yr}.",
      f"SELECT d.MonthNumberOfYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JD} WHERE d.CalendarYear = {yr} GROUP BY d.MonthNumberOfYear ORDER BY d.MonthNumberOfYear",["groupby","month",str(yr)])
    q("dataset", f"What were the top 5 products by sales in {yr}?",
      f"SELECT TOP 5 p.EnglishProductName, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} WHERE YEAR(f.OrderDate) = {yr} GROUP BY p.EnglishProductName ORDER BY SUM(f.SalesAmount) DESC, p.EnglishProductName",["top","product",str(yr)])
    q("dataset", f"Show reseller sales by product colour in {yr}.",
      f"SELECT p.Color, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} WHERE YEAR(f.OrderDate) = {yr} GROUP BY p.Color ORDER BY p.Color",["groupby","color",str(yr)])
    q("multivalue", f"Summarise {yr}: orders, units, sales, and profit.",
      f"SELECT COUNT(DISTINCT f.SalesOrderNumber) AS Orders, SUM(f.OrderQuantity) AS Units, SUM(f.SalesAmount) AS Sales, SUM(f.SalesAmount) - SUM(f.TotalProductCost) AS Profit FROM {F} f WHERE YEAR(f.OrderDate) = {yr}",["summary",str(yr)])

for country in ["United States","Canada","France","Germany","United Kingdom","Australia"]:
    slug = country.replace(" ","").lower()
    q("dataset", f"Show reseller sales by year in {country}.",
      f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} WHERE t.SalesTerritoryCountry = '{country}' GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["groupby","year",slug])
    q("dataset", f"What are the top 5 products sold in {country}?",
      f"SELECT TOP 5 p.EnglishProductName, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} {JT} WHERE t.SalesTerritoryCountry = '{country}' GROUP BY p.EnglishProductName ORDER BY SUM(f.SalesAmount) DESC, p.EnglishProductName",["top","product",slug])
    q("dataset", f"Show reseller sales by region within {country}.",
      f"SELECT t.SalesTerritoryRegion, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} WHERE t.SalesTerritoryCountry = '{country}' GROUP BY t.SalesTerritoryRegion ORDER BY t.SalesTerritoryRegion",["groupby","region",slug])
    q("multivalue", f"Summarise sales, units, and orders for {country}.",
      f"SELECT SUM(f.SalesAmount) AS Sales, SUM(f.OrderQuantity) AS Units, COUNT(DISTINCT f.SalesOrderNumber) AS Orders FROM {F} f {JT} WHERE t.SalesTerritoryCountry = '{country}'",["summary",slug])
    q("scalar", f"How many reseller sales orders came from {country}?",
      f"SELECT COUNT(DISTINCT f.SalesOrderNumber) AS Value FROM {F} f {JT} WHERE t.SalesTerritoryCountry = '{country}'",["count",slug])

for bt in ["Specialty Bike Shop","Value Added Reseller","Warehouse"]:
    slug = bt.replace(" ","").lower()
    q("dataset", f"Show sales by year for {bt} resellers.",
      f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} WHERE r.BusinessType = '{bt}' GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["groupby","year",slug])
    q("dataset", f"Which 5 {bt} resellers sold the most?",
      f"SELECT TOP 5 r.ResellerName, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} WHERE r.BusinessType = '{bt}' GROUP BY r.ResellerName ORDER BY SUM(f.SalesAmount) DESC, r.ResellerName",["top","reseller",slug])
    q("dataset", f"Show sales by country for {bt} resellers.",
      f"SELECT t.SalesTerritoryCountry, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} {JT} WHERE r.BusinessType = '{bt}' GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["groupby","country",slug])

for grp in ["Europe","North America","Pacific"]:
    slug = grp.replace(" ","").lower()
    q("dataset", f"Show reseller sales by year in {grp}.",
      f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} WHERE t.SalesTerritoryGroup = '{grp}' GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["groupby","year",slug])
    q("dataset", f"Show sales by business type in {grp}.",
      f"SELECT r.BusinessType, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JR} {JT} WHERE t.SalesTerritoryGroup = '{grp}' GROUP BY r.BusinessType ORDER BY r.BusinessType",["groupby","biztype",slug])
    q("multivalue", f"Summarise sales and profit for {grp}.",
      f"SELECT SUM(f.SalesAmount) AS Sales, SUM(f.TotalProductCost) AS Cost, SUM(f.SalesAmount) - SUM(f.TotalProductCost) AS Profit FROM {F} f {JT} WHERE t.SalesTerritoryGroup = '{grp}'",["profit",slug])

for color in ["Black","Red","Silver","Blue","Yellow"]:
    slug = color.lower()
    q("dataset", f"Show sales of {color} products by year.",
      f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} WHERE p.Color = '{color}' GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["groupby","year",slug])
    q("dataset", f"What are the top 5 {color} products by sales?",
      f"SELECT TOP 5 p.EnglishProductName, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} WHERE p.Color = '{color}' GROUP BY p.EnglishProductName ORDER BY SUM(f.SalesAmount) DESC, p.EnglishProductName",["top","product",slug])
    q("scalar", f"How many units of {color} products were sold?",
      f"SELECT SUM(f.OrderQuantity) AS Value FROM {F} f {JP} WHERE p.Color = '{color}'",["sum",slug])

# ---- report-derived questions (the AdventureWorks Sales .pbix visuals) ---------------------------------------
q("dataset","What is sales amount by category and reseller business type?",
  f"SELECT p.ProductLine, r.BusinessType, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} {JR} GROUP BY p.ProductLine, r.BusinessType ORDER BY p.ProductLine, r.BusinessType",["report","crosstab"])
q("dataset","What is order quantity by reseller country?",
  f"SELECT t.SalesTerritoryCountry, SUM(f.OrderQuantity) AS OrderQuantity FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["report","units"])
q("dataset","What is sales amount by order date year?",
  f"SELECT d.CalendarYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JD} GROUP BY d.CalendarYear ORDER BY d.CalendarYear",["report","year"])
q("dataset","What is sales amount by due date year?",
  f"SELECT d.CalendarYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f JOIN {D} d ON d.DateKey = f.DueDateKey GROUP BY d.CalendarYear ORDER BY d.CalendarYear",["report","duedate"])
q("dataset","What is sales amount by ship date year?",
  f"SELECT d.CalendarYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f JOIN {D} d ON d.DateKey = f.ShipDateKey GROUP BY d.CalendarYear ORDER BY d.CalendarYear",["report","shipdate"])

# ---- edge cases and guards --------------------------------------------------------------------------------
q("dataset","Show reseller sales for a country that does not exist.",
  f"SELECT t.SalesTerritoryCountry, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JT} WHERE t.SalesTerritoryCountry = 'Atlantis' GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["empty","edge"])
q("scalar","What were reseller sales in 1999?",
  f"SELECT SUM(f.SalesAmount) AS Value FROM {F} f WHERE YEAR(f.OrderDate) = 1999",["null","edge"])
q("scalar","How many products have the colour 'NA' recorded?",
  f"SELECT COUNT(*) AS Value FROM {P} WHERE Color = 'NA'",["edge","placeholder"])
q("scalar","How many sales territories are the 'NA' placeholder?",
  f"SELECT COUNT(*) AS Value FROM {T} WHERE SalesTerritoryCountry = 'NA'",["edge","placeholder"])
q("scalar","How many products have no product line assigned?",
  f"SELECT COUNT(*) AS Value FROM {P} WHERE ProductLine IS NULL",["null","edge"])
q("scalar","How many products have no subcategory?",
  f"SELECT COUNT(*) AS Value FROM {P} WHERE ProductSubcategoryKey IS NULL",["null","edge"])
q("scalar","How many resellers have no recorded bank?",
  f"SELECT COUNT(*) AS Value FROM {R} WHERE BankName IS NULL",["null","edge"])
q("scalar","How many reseller sales lines have no carrier tracking number?",
  f"SELECT COUNT(*) AS Value FROM {F} f WHERE f.CarrierTrackingNumber IS NULL",["null","edge"])
q("dataset","List products whose name contains 'Mountain'.",
  f"SELECT TOP 20 EnglishProductName, ListPrice FROM {P} WHERE EnglishProductName LIKE '%Mountain%' ORDER BY EnglishProductName",["like","edge"])
q("dataset","List products whose name contains 'Road'.",
  f"SELECT TOP 20 EnglishProductName, ListPrice FROM {P} WHERE EnglishProductName LIKE '%Road%' ORDER BY EnglishProductName",["like","edge"])
q("dataset","List resellers whose name starts with 'A'.",
  f"SELECT TOP 20 ResellerName, BusinessType FROM {R} WHERE ResellerName LIKE 'A%' ORDER BY ResellerName",["like","edge"])
q("scalar","How many product names contain the word 'Bike'?",
  f"SELECT COUNT(*) AS Value FROM {P} WHERE EnglishProductName LIKE '%Bike%'",["like","edge"])

# ---- deeper analytics ------------------------------------------------------------------------------------
q("dataset","Show each territory group's share of total reseller sales.",
  f"SELECT t.SalesTerritoryGroup, SUM(f.SalesAmount) * 100.0 / NULLIF((SELECT SUM(SalesAmount) FROM {F}), 0) AS PctOfTotal FROM {F} f {JT} GROUP BY t.SalesTerritoryGroup ORDER BY t.SalesTerritoryGroup",["share","window"])
q("dataset","Rank countries by reseller sales.",
  f"SELECT t.SalesTerritoryCountry, SUM(f.SalesAmount) AS SalesAmount, RANK() OVER (ORDER BY SUM(f.SalesAmount) DESC) AS SalesRank FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry ORDER BY SalesRank, t.SalesTerritoryCountry",["rank","window"])
q("dataset","Show year over year reseller sales with the previous year alongside.",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount, LAG(SUM(f.SalesAmount)) OVER (ORDER BY YEAR(f.OrderDate)) AS PriorYear FROM {F} f GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["lag","window"])
q("dataset","Show a running total of reseller sales by year.",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount, SUM(SUM(f.SalesAmount)) OVER (ORDER BY YEAR(f.OrderDate) ROWS UNBOUNDED PRECEDING) AS RunningTotal FROM {F} f GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["running","window"])
q("dataset","Show the average sale amount per order by year.",
  f"SELECT v.OrderYear, AVG(v.OrderTotal) AS AvgOrderValue FROM (SELECT YEAR(f.OrderDate) AS OrderYear, f.SalesOrderNumber, SUM(f.SalesAmount) AS OrderTotal FROM {F} f GROUP BY YEAR(f.OrderDate), f.SalesOrderNumber) v GROUP BY v.OrderYear ORDER BY v.OrderYear",["subquery","avg"])
q("dataset","Which resellers ordered in every year from 2011 to 2013?",
  f"SELECT TOP 20 r.ResellerName FROM {F} f {JR} WHERE YEAR(f.OrderDate) BETWEEN 2011 AND 2013 GROUP BY r.ResellerName HAVING COUNT(DISTINCT YEAR(f.OrderDate)) = 3 ORDER BY r.ResellerName",["having","edge"])
q("dataset","Which product lines sold more than one million in total?",
  f"SELECT p.ProductLine, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} GROUP BY p.ProductLine HAVING SUM(f.SalesAmount) > 1000000 ORDER BY p.ProductLine",["having"])
q("dataset","Which countries sold more than 5000 units?",
  f"SELECT t.SalesTerritoryCountry, SUM(f.OrderQuantity) AS Units FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry HAVING SUM(f.OrderQuantity) > 5000 ORDER BY t.SalesTerritoryCountry",["having"])
q("dataset","Show margin percentage by product line.",
  f"SELECT p.ProductLine, (SUM(f.SalesAmount) - SUM(f.TotalProductCost)) * 100.0 / NULLIF(SUM(f.SalesAmount), 0) AS MarginPct FROM {F} f {JP} GROUP BY p.ProductLine ORDER BY p.ProductLine",["margin"])
q("dataset","Show margin percentage by country.",
  f"SELECT t.SalesTerritoryCountry, (SUM(f.SalesAmount) - SUM(f.TotalProductCost)) * 100.0 / NULLIF(SUM(f.SalesAmount), 0) AS MarginPct FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["margin"])
q("dataset","Show the ten product models with the highest average unit price.",
  f"SELECT TOP 10 p.ModelName, AVG(f.UnitPrice) AS AvgUnitPrice FROM {F} f {JP} WHERE p.ModelName IS NOT NULL GROUP BY p.ModelName ORDER BY AVG(f.UnitPrice) DESC, p.ModelName",["top","model"])
q("dataset","Show total discount given by year.",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, SUM(f.DiscountAmount) AS Discount FROM {F} f GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["groupby","discount"])
q("dataset","Show freight cost by territory group.",
  f"SELECT t.SalesTerritoryGroup, SUM(f.Freight) AS Freight FROM {F} f {JT} GROUP BY t.SalesTerritoryGroup ORDER BY t.SalesTerritoryGroup",["groupby","freight"])
q("dataset","Show tax collected by country.",
  f"SELECT t.SalesTerritoryCountry, SUM(f.TaxAmt) AS Tax FROM {F} f {JT} GROUP BY t.SalesTerritoryCountry ORDER BY t.SalesTerritoryCountry",["groupby","tax"])
q("dataset","How many distinct products did each territory group sell?",
  f"SELECT t.SalesTerritoryGroup, COUNT(DISTINCT f.ProductKey) AS Products FROM {F} f {JT} GROUP BY t.SalesTerritoryGroup ORDER BY t.SalesTerritoryGroup",["distinct","groupby"])
q("dataset","How many resellers ordered in each year?",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, COUNT(DISTINCT f.ResellerKey) AS Resellers FROM {F} f GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["distinct","groupby"])
q("dataset","Show the number of products by class and style.",
  f"SELECT Class, Style, COUNT(*) AS Products FROM {P} GROUP BY Class, Style ORDER BY Class, Style",["groupby","crosstab"])
q("dataset","Show average days between order and ship date by year.",
  f"SELECT YEAR(f.OrderDate) AS OrderYear, AVG(CAST(DATEDIFF(day, f.OrderDate, f.ShipDate) AS float)) AS AvgShipDays FROM {F} f WHERE f.ShipDate IS NOT NULL GROUP BY YEAR(f.OrderDate) ORDER BY OrderYear",["datediff"])
q("scalar","What is the average number of days between order and shipment?",
  f"SELECT AVG(CAST(DATEDIFF(day, f.OrderDate, f.ShipDate) AS float)) AS Value FROM {F} f WHERE f.ShipDate IS NOT NULL",["datediff"])
q("scalar","How many reseller sales lines shipped after their due date?",
  f"SELECT COUNT(*) AS Value FROM {F} f WHERE f.ShipDate > f.DueDate",["late","edge"])
q("multivalue","Summarise shipping performance: total lines, late lines, and late percentage.",
  f"SELECT COUNT(*) AS Lines, SUM(CASE WHEN f.ShipDate > f.DueDate THEN 1 ELSE 0 END) AS LateLines, SUM(CASE WHEN f.ShipDate > f.DueDate THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS LatePct FROM {F} f",["late","ratio"])
q("multivalue","What is the overall discount picture: total discount, discounted lines, and average discount?",
  f"SELECT SUM(f.DiscountAmount) AS TotalDiscount, SUM(CASE WHEN f.DiscountAmount > 0 THEN 1 ELSE 0 END) AS DiscountedLines, AVG(f.DiscountAmount) AS AvgDiscount FROM {F} f",["discount"])
q("multivalue","Give the product catalogue summary: total, finished goods, and with a list price.",
  f"SELECT COUNT(*) AS Products, SUM(CASE WHEN FinishedGoodsFlag = 1 THEN 1 ELSE 0 END) AS FinishedGoods, SUM(CASE WHEN ListPrice IS NOT NULL THEN 1 ELSE 0 END) AS WithListPrice FROM {P}",["summary","dimproduct"])
q("multivalue","Summarise the reseller base: total, with sales, and without.",
  f"SELECT COUNT(*) AS Resellers, SUM(v.HasSales) AS WithSales, SUM(1 - v.HasSales) AS WithoutSales FROM (SELECT CASE WHEN EXISTS (SELECT 1 FROM {F} f WHERE f.ResellerKey = r.ResellerKey) THEN 1 ELSE 0 END AS HasSales FROM {R} r) v",["summary","dimreseller"])
q("dataset","Show the first and last order year for the ten busiest resellers.",
  f"SELECT TOP 10 r.ResellerName, MIN(f.OrderDate) AS FirstOrder, MAX(f.OrderDate) AS LastOrder, COUNT(*) AS Lines FROM {F} f {JR} GROUP BY r.ResellerName ORDER BY COUNT(*) DESC, r.ResellerName",["top","range"])
q("dataset","Show sales by product line and year.",
  f"SELECT p.ProductLine, YEAR(f.OrderDate) AS OrderYear, SUM(f.SalesAmount) AS SalesAmount FROM {F} f {JP} GROUP BY p.ProductLine, YEAR(f.OrderDate) ORDER BY p.ProductLine, OrderYear",["groupby","crosstab"])
q("dataset","Show units sold by product colour and year.",
  f"SELECT p.Color, YEAR(f.OrderDate) AS OrderYear, SUM(f.OrderQuantity) AS Units FROM {F} f {JP} GROUP BY p.Color, YEAR(f.OrderDate) ORDER BY p.Color, OrderYear",["groupby","crosstab"])
q("dataset","Which ten products had the widest gap between list price and standard cost?",
  f"SELECT TOP 10 EnglishProductName, ListPrice, StandardCost, ListPrice - StandardCost AS Markup FROM {P} WHERE ListPrice IS NOT NULL AND StandardCost IS NOT NULL ORDER BY ListPrice - StandardCost DESC, EnglishProductName",["top","markup"])
q("dataset","Show reseller counts by product line they carry.",
  f"SELECT ProductLine, COUNT(*) AS Resellers FROM {R} GROUP BY ProductLine ORDER BY ProductLine",["groupby","dimreseller"])
q("dataset","Show the number of customers by number of cars owned.",
  f"SELECT NumberCarsOwned, COUNT(*) AS Customers FROM {C} GROUP BY NumberCarsOwned ORDER BY NumberCarsOwned",["groupby","dimcustomer"])

import io, os

# These grouped queries return exactly one row against this sample (each of these countries has a single
# territory region, and reseller sales in 2010 span one month), so they are declared by what they return.
SINGLE_ROW = {
    "Show monthly reseller sales for 2010.",
    "Show reseller sales by region within Canada.",
    "Show reseller sales by region within France.",
    "Show reseller sales by region within Germany.",
    "Show reseller sales by region within United Kingdom.",
    "Show reseller sales by region within Australia.",
}
for _q in Q:
    if _q["question"] in SINGLE_ROW:
        _q["shape"] = "multivalue"

assert len(Q) == 300, len(Q)
ids = [x["id"] for x in Q]
assert len(set(ids)) == len(ids), "duplicate ids"
qs = [x["question"] for x in Q]
dupes = {x for x in qs if qs.count(x) > 1}
assert not dupes, f"duplicate questions: {dupes}"
with open(os.path.join(os.path.dirname(__file__), "questions.json"), "w", encoding="utf-8") as fh:
    json.dump(Q, fh, indent=2, ensure_ascii=False)
print("wrote questions.json:", len(Q))
