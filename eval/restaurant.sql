CREATE DATABASE restaurant_db;
USE restaurant_db;
CREATE TABLE outlet (outlet_no INT PRIMARY KEY, title VARCHAR(40), area VARCHAR(30), opened DATE, monthly_rent INT);
CREATE TABLE staff (staff_no INT PRIMARY KEY, full_name VARCHAR(40), duty VARCHAR(20), pay INT, joined DATE, outlet_no INT, reports_to INT,
  FOREIGN KEY (outlet_no) REFERENCES outlet(outlet_no), FOREIGN KEY (reports_to) REFERENCES staff(staff_no));
CREATE TABLE dish (dish_code VARCHAR(5) PRIMARY KEY, dish_name VARCHAR(40), course VARCHAR(20), price INT, calories INT);
CREATE TABLE bill (bill_no INT PRIMARY KEY, outlet_no INT, staff_no INT, bill_date DATE, pay_mode VARCHAR(15), tip INT,
  FOREIGN KEY (outlet_no) REFERENCES outlet(outlet_no), FOREIGN KEY (staff_no) REFERENCES staff(staff_no));
CREATE TABLE bill_item (bill_no INT, dish_code VARCHAR(5), qty INT, PRIMARY KEY (bill_no, dish_code),
  FOREIGN KEY (bill_no) REFERENCES bill(bill_no), FOREIGN KEY (dish_code) REFERENCES dish(dish_code));
INSERT INTO outlet VALUES (1,'Lakeside','Gulshan','2018-03-01',90000),(2,'Old Town','Wari','2020-07-15',40000),(3,'Skyline','Banani','2021-01-10',120000),(4,'Garden','Uttara','2024-05-05',60000);
INSERT INTO staff VALUES (1,'Rafi','Manager',60000,'2018-03-01',1,NULL),(2,'Sumi','Waiter',22000,'2019-06-01',1,1),(3,'Tuhin','Chef',45000,'2018-04-01',1,1),
 (4,'Urmi','Manager',52000,'2020-07-15',2,NULL),(5,'Vikram','Waiter',20000,'2021-02-01',2,4),(6,'Wasim','Chef',40000,'2022-02-01',2,4),
 (7,'Xena','Manager',65000,'2021-01-10',3,NULL),(8,'Yasin','Waiter',25000,'2022-09-01',3,7),(9,'Zara','Chef',50000,'2023-03-01',3,7),(10,'Bokul','Cleaner',15000,'2023-05-01',NULL,1);
INSERT INTO dish VALUES ('D1','Kacchi','Main',350,900),('D2','Borhani','Drink',80,150),('D3','Firni','Dessert',100,300),('D4','Beef Rezala','Main',280,700),
 ('D5','Lassi','Drink',120,250),('D6','Salad','Starter',90,NULL),('D7','Soup','Starter',150,180);
INSERT INTO bill VALUES (101,1,2,'2024-01-05','Cash',50),(102,1,2,'2024-01-20','Card',100),(103,2,5,'2024-02-11','Cash',NULL),(104,3,8,'2024-02-14','Card',200),
 (105,3,8,'2024-03-01','Bkash',80),(106,2,5,'2025-01-03','Card',30),(107,1,3,'2025-02-02','Cash',NULL);
INSERT INTO bill_item VALUES (101,'D1',2),(101,'D2',2),(102,'D4',1),(102,'D5',3),(103,'D1',4),(104,'D1',3),(104,'D3',3),(105,'D7',2),(106,'D2',5),(106,'D3',1),(107,'D4',2);
