T = {}
T['company'] = [
("Retrieve the first name, last name and address of all employees who work for the 'Research' department.", "SELECT Fname,Lname,Address FROM EMPLOYEE JOIN DEPARTMENT ON Dno=Dnumber WHERE Dname='Research'", ""),
("For every project located in 'Stafford', list the project number, the controlling department number, and the department manager's last name, address and birth date.", "SELECT Pnumber,Dnum,Lname,Address,Bdate FROM PROJECT JOIN DEPARTMENT ON Dnum=Dnumber JOIN EMPLOYEE ON Mgr_ssn=Ssn WHERE Plocation='Stafford'", ""),
("List the first and last name of each employee and the last name of his or her supervisor (aliased as Boss). Include employees without a supervisor.", "SELECT e.Fname,e.Lname,s.Lname FROM EMPLOYEE e LEFT JOIN EMPLOYEE s ON e.Super_ssn=s.Ssn", ""),
("Find the sum of the salaries, the maximum salary, the minimum salary and the average salary of employees of the 'Research' department.", "SELECT SUM(Salary),MAX(Salary),MIN(Salary),AVG(Salary) FROM EMPLOYEE JOIN DEPARTMENT ON Dno=Dnumber WHERE Dname='Research'", ""),
("For each project, retrieve the project number, the project name and the number of employees who work on that project.", "SELECT Pnumber,Pname,COUNT(*) FROM PROJECT JOIN WORKS_ON ON Pno=Pnumber GROUP BY Pnumber,Pname", ""),
("For each project on which more than two employees work, retrieve the project name and the number of employees.", "SELECT Pname,COUNT(*) FROM PROJECT JOIN WORKS_ON ON Pno=Pnumber GROUP BY Pnumber,Pname HAVING COUNT(*)>2", ""),
("Retrieve the first and last names of employees who have no dependents.", "SELECT Fname,Lname FROM EMPLOYEE WHERE NOT EXISTS(SELECT 1 FROM DEPENDENT WHERE Essn=Ssn)", ""),
("List the first and last names of managers who have at least one dependent.", "SELECT DISTINCT Fname,Lname FROM EMPLOYEE JOIN DEPARTMENT ON Ssn=Mgr_ssn JOIN DEPENDENT ON Essn=Ssn", ""),
("Retrieve all employees whose address contains 'Houston'. Show first name and last name only.", "SELECT Fname,Lname FROM EMPLOYEE WHERE Address LIKE '%Houston%'", ""),
("Show the department name and the total hours worked on projects controlled by that department.", "SELECT Dname,SUM(Hours) FROM DEPARTMENT JOIN PROJECT ON Dnum=Dnumber JOIN WORKS_ON ON Pno=Pnumber GROUP BY Dname", ""),
("Find the first name and salary of employees who earn more than every employee in department 4.", "SELECT Fname,Salary FROM EMPLOYEE WHERE Salary>ALL(SELECT Salary FROM EMPLOYEE WHERE Dno=4)", ""),
("Give all employees working in the Research department a 10 percent raise.", "UPDATE EMPLOYEE SET Salary=Salary*1.1 WHERE Dno=5", "SELECT Ssn,Salary FROM EMPLOYEE"),
("Create a view named Dept_Summary that shows each department name, number of employees and average salary.", "CREATE VIEW Dept_Summary AS SELECT Dname,COUNT(*) n,AVG(Salary) a FROM DEPARTMENT JOIN EMPLOYEE ON Dno=Dnumber GROUP BY Dname", "SELECT * FROM Dept_Summary"),
("Retrieve the SSNs of employees who work on project number 1 but not on project number 2, using EXCEPT.", "SELECT Essn FROM WORKS_ON WHERE Pno=1 EXCEPT SELECT Essn FROM WORKS_ON WHERE Pno=2", ""),
("Show last name, salary and a column Band that is 'Low' when salary is below 30000, 'Mid' when it is from 30000 to 40000 and 'High' otherwise.", "SELECT Lname,Salary,CASE WHEN Salary<30000 THEN 'Low' WHEN Salary<=40000 THEN 'Mid' ELSE 'High' END FROM EMPLOYEE", ""),
("Delete all dependents of the employee with SSN 123456789.", "DELETE FROM DEPENDENT WHERE Essn='123456789'", "SELECT * FROM DEPENDENT"),
]
T['hospital'] = [
("Show the name and city of patients from Dhaka, sorted by name.", "SELECT name,city FROM patients WHERE city='Dhaka' ORDER BY name -- ORDERED", ""),
("Count the number of doctors in each specialty, showing only specialties that have more than 1 doctor.", "SELECT specialty,COUNT(*) FROM doctors GROUP BY specialty HAVING COUNT(*)>1", ""),
("List every doctor's name with the number of appointments they have, including doctors with zero appointments.", "SELECT full_name,COUNT(appointment_id) FROM doctors d LEFT JOIN appointments a ON a.doctor_id=d.doctor_id GROUP BY d.doctor_id,full_name", ""),
("Show patient name, doctor name and appointment date for all completed appointments, most recent first.", "SELECT name,full_name,appointment_date FROM appointments a JOIN patients p ON a.patient_id=p.patient_id JOIN doctors d ON d.doctor_id=a.doctor_id WHERE status='Completed' ORDER BY appointment_date DESC -- ORDERED", ""),
("Find patients who have never had an appointment. Show their names.", "SELECT name FROM patients p WHERE NOT EXISTS(SELECT 1 FROM appointments a WHERE a.patient_id=p.patient_id)", ""),
("What is the total cost of medicines prescribed in each appointment? Show appointment_id and the total as Total_Cost.", "SELECT appointment_id,SUM(quantity*unit_price) FROM prescriptions p JOIN medicines m ON m.medicine_id=p.medicine_id GROUP BY appointment_id", ""),
("Show the second highest consultation fee.", "SELECT DISTINCT consultation_fee FROM doctors ORDER BY 1 DESC LIMIT 1 OFFSET 1", ""),
("List doctors whose consultation fee is higher than the average fee. Show full_name and consultation_fee.", "SELECT full_name,consultation_fee FROM doctors WHERE consultation_fee>(SELECT AVG(consultation_fee) FROM doctors)", ""),
("Show the name of patients who were prescribed the medicine 'Losartan'.", "SELECT DISTINCT name FROM patients p JOIN appointments a ON a.patient_id=p.patient_id JOIN prescriptions r ON r.appointment_id=a.appointment_id JOIN medicines m ON m.medicine_id=r.medicine_id WHERE medicine_name='Losartan'", ""),
("Increase the unit price of all medicines that cost less than 10 by 20%.", "UPDATE medicines SET unit_price=unit_price*1.2 WHERE unit_price<10", "SELECT * FROM medicines"),
("Insert a new patient with id 7, name 'Test User', gender 'M', birth date 2000-01-01, city 'Bogura' and blood group 'B-'.", "INSERT INTO patients VALUES(7,'Test User','M','2000-01-01','Bogura','B-')", "SELECT * FROM patients"),
("Add a column email of type VARCHAR(80) to the patients table.", "ALTER TABLE patients ADD email VARCHAR(80)", "SHOW COLUMNS FROM patients"),
("Change the data type of medicine_name to VARCHAR(100).", "ALTER TABLE medicines MODIFY medicine_name VARCHAR(100)", "SELECT column_type FROM information_schema.columns WHERE table_schema=DATABASE() AND column_name='medicine_name'"),
("Show all details of patients whose city is not recorded.", "SELECT * FROM patients WHERE city IS NULL", ""),
("For each specialty show the doctor with the highest consultation fee (specialty, full_name, consultation_fee).", "SELECT specialty,full_name,consultation_fee FROM doctors d WHERE consultation_fee=(SELECT MAX(consultation_fee) FROM doctors x WHERE x.specialty=d.specialty)", ""),
("Create a trigger named trg_stock that reduces the stock of a medicine by the prescribed quantity after a row is inserted into prescriptions.", "CREATE TRIGGER trg_stock AFTER INSERT ON prescriptions FOR EACH ROW UPDATE medicines SET stock=stock-NEW.quantity WHERE medicine_id=NEW.medicine_id", "INSERT INTO prescriptions VALUES(3,5,50); SELECT medicine_id,stock FROM medicines"),
("Create a stored procedure GetPatientsByCity that takes a city as IN parameter and returns the patients of that city.", "DELIMITER $$\nCREATE PROCEDURE GetPatientsByCity(IN c VARCHAR(30)) BEGIN SELECT * FROM patients WHERE city=c; END $$\nDELIMITER ;", "CALL GetPatientsByCity('Khulna')"),
("Display the number of appointments per month in 2024.", "SELECT MONTH(appointment_date),COUNT(*) FROM appointments WHERE YEAR(appointment_date)=2024 GROUP BY 1", ""),
]
T['lab6'] = [
("Show the First_Name, Last_Name of employees and the Course_Name they completed with a score above 85.", "SELECT First_Name,Last_Name,Course_Name FROM employee e JOIN training_enrollment t ON e.Employee_ID=t.Employee_ID JOIN training_course c ON c.Course_ID=t.Course_ID WHERE t.Status='Completed' AND Score>85", ""),
("Display Customer_Name and the total order amount (aliased as Total_Spent) for customers whose total is more than 80,000. Sort by Total_Spent descending.", "SELECT Customer_Name,SUM(Total_Amount) t FROM customer c JOIN sales_order o ON c.Customer_ID=o.Customer_ID GROUP BY c.Customer_ID,Customer_Name HAVING t>80000 ORDER BY t DESC -- ORDERED", ""),
("Display the Dept_Name and the first name of its manager. Include departments that have no manager.", "SELECT Dept_Name,First_Name FROM department d LEFT JOIN employee e ON d.Manager_ID=e.Employee_ID", ""),
("List employees who earn more than the average salary of their own department. Show First_Name, Dept_ID and Salary_Amount.", "SELECT First_Name,Dept_ID,Salary_Amount FROM employee e WHERE Salary_Amount>(SELECT AVG(Salary_Amount) FROM employee x WHERE x.Dept_ID=e.Dept_ID)", ""),
("Find all employees hired in 2024 whose email is a gmail address. Show First_Name, Email_Address, Hire_Date.", "SELECT First_Name,Email_Address,Hire_Date FROM employee WHERE YEAR(Hire_Date)=2024 AND Email_Address LIKE '%gmail%'", ""),
("Show Payment_Method, number of orders (aliased as Order_Count) and average amount (aliased as Avg_Amount) for delivered orders only.", "SELECT Payment_Method,COUNT(*),AVG(Total_Amount) FROM sales_order WHERE Order_Status='Delivered' GROUP BY Payment_Method", ""),
("Display each employee's First_Name, Salary_Amount and Grade_Label from the salary_grade table based on the salary range.", "SELECT First_Name,Salary_Amount,Grade_Label FROM employee JOIN salary_grade ON Salary_Amount BETWEEN Min_Salary AND Max_Salary", ""),
("List the top 3 projects by total hours worked. Show Project_Name and Total_Hours.", "SELECT Project_Name,SUM(Hours_Worked) h FROM project p JOIN project_assignment a ON a.Project_ID=p.Project_ID GROUP BY p.Project_ID,Project_Name ORDER BY h DESC LIMIT 3", ""),
("Simulate a FULL OUTER JOIN between employee and department to display First_Name and Dept_Name.", "SELECT First_Name,Dept_Name FROM employee e LEFT JOIN department d ON e.Dept_ID=d.Dept_ID UNION SELECT First_Name,Dept_Name FROM employee e RIGHT JOIN department d ON e.Dept_ID=d.Dept_ID", ""),
("Find departments that have no projects with status 'Active'. Show Dept_Name.", "SELECT Dept_Name FROM department d WHERE NOT EXISTS(SELECT 1 FROM project p WHERE p.Dept_ID=d.Dept_ID AND Status='Active')", ""),
("Delete all training enrollments whose score is below 60.", "DELETE FROM training_enrollment WHERE Score<60", "SELECT * FROM training_enrollment"),
("Create a trigger that prevents the deletion of a department that still has employees.", "DELIMITER $$\nCREATE TRIGGER t BEFORE DELETE ON department FOR EACH ROW BEGIN IF EXISTS(SELECT 1 FROM employee WHERE Dept_ID=OLD.Dept_ID) THEN SIGNAL SQLSTATE '45000'; END IF; END $$\nDELIMITER ;", "DELETE IGNORE FROM department WHERE Dept_ID=999; DELETE FROM department WHERE Dept_ID=10; SELECT COUNT(*) FROM department"),
("Write a stored function GetAnnualSalary that takes an employee id and returns the annual salary (monthly salary times 12).", "CREATE FUNCTION GetAnnualSalary(i INT) RETURNS DECIMAL(12,2) DETERMINISTIC RETURN (SELECT Salary_Amount*12 FROM employee WHERE Employee_ID=i)", "SELECT GetAnnualSalary(102)"),
("Which employees have supervised at least 3 people? Show the supervisor's First_Name and the number of people supervised.", "SELECT s.First_Name,COUNT(*) FROM employee e JOIN employee s ON e.Supervisor_ID=s.Employee_ID GROUP BY s.Employee_ID,s.First_Name HAVING COUNT(*)>=3", ""),
]
T['library'] = [
("List the title and price of all books priced between 300 and 600, cheapest first.", "SELECT Title,Price FROM Book WHERE Price BETWEEN 300 AND 600 ORDER BY Price -- ORDERED", ""),
("Show each genre with the number of books (aliased as Book_Count) and average price (aliased as Avg_Price). Keep only genres with at least 2 books.", "SELECT Genre,COUNT(*),AVG(Price) FROM Book GROUP BY Genre HAVING COUNT(*)>=2", ""),
("Display the member name and the title of each book that has not been returned yet.", "SELECT Name,Title FROM Loan l JOIN Member m ON l.MemberID=m.MemberID JOIN Book b ON b.BookID=l.BookID WHERE ReturnDate IS NULL", ""),
("Find authors who have no books in the library. Show AuthorName.", "SELECT AuthorName FROM Author a WHERE NOT EXISTS(SELECT 1 FROM Book b WHERE b.AuthorID=a.AuthorID)", ""),
("Show the most expensive book: title and price.", "SELECT Title,Price FROM Book WHERE Price=(SELECT MAX(Price) FROM Book)", ""),
("Using a LEFT JOIN, show every member's Name and how many loans they made (aliased as Total_Loans).", "SELECT Name,COUNT(LoanID) FROM Member m LEFT JOIN Loan l ON l.MemberID=m.MemberID GROUP BY m.MemberID,Name", ""),
("Show the titles of books borrowed by 'Student' type members.", "SELECT DISTINCT Title FROM Loan l JOIN Member m ON l.MemberID=m.MemberID JOIN Book b ON b.BookID=l.BookID WHERE MemberType='Student'", ""),
("Reduce the price of all 'Fiction' books by 50.", "UPDATE Book SET Price=Price-50 WHERE Genre='Fiction'", "SELECT * FROM Book"),
("Use UNION to list all author countries and all member cities in one column named Place.", "SELECT Country FROM Author UNION SELECT City FROM Member", ""),
("Create a table Fine with Fine_ID as auto increment primary key, LoanID as a foreign key to Loan and Amount DECIMAL(8,2) not null.", "CREATE TABLE Fine (Fine_ID INT AUTO_INCREMENT PRIMARY KEY, LoanID INT, Amount DECIMAL(8,2) NOT NULL, FOREIGN KEY (LoanID) REFERENCES Loan(LoanID))", "SELECT column_name,column_type,column_key FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='fine'"),
("Show the names of members who borrowed more than one book.", "SELECT Name FROM Member m JOIN Loan l ON l.MemberID=m.MemberID GROUP BY m.MemberID,Name HAVING COUNT(*)>1", ""),
("Create a unique index named idx_title on the Title column of Book.", "CREATE UNIQUE INDEX idx_title ON Book(Title)", "SELECT index_name,non_unique,column_name FROM information_schema.statistics WHERE table_schema=DATABASE() AND table_name='book' AND index_name='idx_title'"),
]
import os
D = os.path.dirname(os.path.abspath(__file__))
for k, v in T.items():
    with open(os.path.join(D, k+'_q.txt'), 'w', encoding='utf8') as f:
        f.write(''.join('%d. %s\n' % (i+1, a) for i, (a, b, c) in enumerate(v)))
    with open(os.path.join(D, k+'_gold.txt'), 'w', encoding='utf8') as f:
        for i, (a, b, c) in enumerate(v):
            f.write('### %d\n%s\n%s\n' % (i, b, '' if b.rstrip().endswith(';') else ';'))
            if c: f.write('-- PROBE\n%s;\n' % c)
